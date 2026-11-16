// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ecl.Core;
using Ecl.Core.Json;
using Ecl.Minecraft.Engine.Identity;
using Ecl.Minecraft.Engine.Transactions;
using Ecl.Minecraft.Instances;
using Ecl.Sessions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ecl.Minecraft.Engine.Snapshots;

/// <summary>
/// 用户快照与恢复（ADR 0048）。快照与事务恢复材料使用不同目录和生命周期：
/// 本服务只管理用户快照；事务备份由 <see cref="InstanceTransactionCoordinator"/> 管理。
/// </summary>
public sealed class InstanceSnapshotService : IInstanceSnapshotService, ISnapshotMaintenanceService
{
    private const int MaxFiles = 200_000;
    private const int MaxSnapshotIdLength = 80;

    private static readonly string[] FullExcludedRoots =
    [
        ".ecl",
        "logs",
        "crash-reports",
        "natives",
        "downloads",
        ".cache",
        ".ecl-modpack.json",
        "eula.txt",
    ];

    private readonly IInstanceService _instances;
    private readonly InstanceTransactionCoordinator _transactions;
    private readonly IInstanceWriteGate _gate;
    private readonly IGameSessionService _sessions;
    private readonly IAppPaths _paths;
    private readonly ILogger<InstanceSnapshotService> _logger;
    private readonly TimeProvider _time;

    public InstanceSnapshotService(
        IInstanceService instances,
        InstanceTransactionCoordinator transactions,
        IInstanceWriteGate gate,
        IGameSessionService sessions,
        IAppPaths paths,
        ILogger<InstanceSnapshotService>? logger = null,
        TimeProvider? time = null)
    {
        _instances = instances;
        _transactions = transactions;
        _gate = gate;
        _sessions = sessions;
        _paths = paths;
        _logger = logger ?? NullLogger<InstanceSnapshotService>.Instance;
        _time = time ?? TimeProvider.System;
    }

    public string SnapshotsRoot => Path.Combine(_paths.Root, "snapshots");

    public SnapshotObjectStore Objects => new(Path.Combine(SnapshotsRoot, "objects"));

    public async Task<Result<SnapshotManifest>> CreateAsync(
        string instanceId,
        SnapshotCreateOptions options,
        IProgress<SnapshotProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!AppPaths.IsValidInstanceId(instanceId))
        {
            return Result<SnapshotManifest>.Fail("snapshot.invalid_instance", "实例 ID 不合法。");
        }

        var instance = await _instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return Result<SnapshotManifest>.Fail("instance.not_found", "实例不存在。");
        }

        var gameDirectory = InstancePaths.GetGameDirectory(_paths, instance);
        if (!Directory.Exists(gameDirectory))
        {
            return Result<SnapshotManifest>.Fail("snapshot.root_missing", "实例游戏目录不存在。");
        }

        var leaseResult = await _gate.AcquireReadAsync(instanceId, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!leaseResult.IsSuccess)
        {
            return Result<SnapshotManifest>.Fail(leaseResult.Error!);
        }

        await using var lease = leaseResult.Value!;

        var running = IsInstanceRunning(instanceId);
        var allowRunning = options.AllowRunningInstance || options.AllowRunningWorld;
        if (running && !allowRunning)
        {
            return Result<SnapshotManifest>.Fail(
                "snapshot.instance_running",
                "实例正在运行；请关闭游戏后重试，或显式允许创建一致性不足的快照。");
        }

        IReadOnlyList<string> roots;
        IReadOnlyList<string> excludes;
        string? worldName;
        try
        {
            (roots, excludes, worldName) = ResolveScope(gameDirectory, options);
        }
        catch (InvalidOperationException exception)
        {
            return Result<SnapshotManifest>.Fail("snapshot.invalid_scope", exception.Message);
        }
        if (roots.Count == 0)
        {
            return Result<SnapshotManifest>.Fail("snapshot.scope_empty", "快照范围内没有可包含的内容。");
        }
        var consistencyWarning = running
            ? "实例在游戏运行期间创建快照，跨文件状态可能不一致；该快照不会被视为可恢复快照。"
            : null;
        if (options.Scope == SnapshotScope.World && worldName is not null)
        {
            var sessionLock = Path.Combine(gameDirectory, "saves", worldName, "session.lock");
            if (File.Exists(sessionLock))
            {
                if (!allowRunning)
                {
                    return Result<SnapshotManifest>.Fail(
                        "snapshot.world_running",
                        "世界似乎仍被游戏占用（存在 session.lock）；请关闭游戏后重试。",
                        worldName);
                }

                consistencyWarning ??= "世界在游戏运行期间创建快照，跨文件状态可能不一致。";
            }
        }

        var snapshotId = CreateSnapshotId();
        var directory = GetSnapshotDirectory(instanceId, snapshotId);
        var objects = Objects;
        var manifestPath = Path.Combine(directory, "manifest.json");
        var files = new List<SnapshotFile>();
        var warnings = new List<string>();
        var incomplete = false;
        long totalBytes = 0;

        var initial = new SnapshotManifest(
            InstanceEngineVersions.SnapshotSchema,
            snapshotId,
            instanceId,
            instance.Name,
            gameDirectory,
            _time.GetUtcNow(),
            options.Scope,
            worldName,
            roots,
            excludes,
            [],
            0,
            SnapshotStatus.Incomplete,
            "快照正在创建。",
            consistencyWarning,
            options.Name,
            options.Origin,
            options.RetentionClass,
            InstancePathIdentity.Normalize(gameDirectory),
            instance.GameDirectory is null ? "EclManaged" : "External",
            "f05-1",
            typeof(InstanceSnapshotService).Assembly.GetName().Version?.ToString(3));
        await WriteManifestAsync(manifestPath, initial, cancellationToken).ConfigureAwait(false);

        try
        {
            var entries = EnumerateScopeFiles(gameDirectory, roots, excludes, warnings, cancellationToken);
            var processed = 0;
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                processed++;
                progress?.Report(new SnapshotProgress("snapshot", processed, entries.Count, entry.RelativePath));
                try
                {
                    var hash = SnapshotObjectStore.ComputeHash(entry.FullPath);
                    await objects.PutAsync(entry.FullPath, hash, entry.Size, cancellationToken).ConfigureAwait(false);
                    files.Add(new SnapshotFile(entry.RelativePath, entry.Size, hash, entry.ModifiedAt));
                    totalBytes += entry.Size;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                                      or InvalidDataException)
                {
                    incomplete = true;
                    warnings.Add($"{entry.RelativePath} 无法读取：{exception.Message}");
                }
            }

            var consistent = !incomplete && string.IsNullOrWhiteSpace(consistencyWarning);
            var manifest = initial with
            {
                Files = files,
                TotalBytes = totalBytes,
                Status = consistent ? SnapshotStatus.Complete : SnapshotStatus.Incomplete,
                Note = incomplete
                    ? $"快照不完整：{warnings.Count} 个文件无法读取。"
                    : string.IsNullOrWhiteSpace(consistencyWarning)
                        ? null
                        : "一致性不足：该快照仅作为证据，不用于恢复。",
            };
            await WriteManifestAsync(manifestPath, manifest, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "实例 {InstanceId} 创建快照 {SnapshotId}：{Files} 个文件，{Bytes} 字节，状态 {Status}。",
                instanceId,
                snapshotId,
                files.Count,
                totalBytes,
                manifest.Status);
            return Result<SnapshotManifest>.Ok(manifest);
        }
        catch (OperationCanceledException)
        {
            initial = initial with
            {
                Files = files,
                TotalBytes = totalBytes,
                Status = SnapshotStatus.Incomplete,
                Note = "快照创建被取消。",
            };
            await WriteManifestAsync(manifestPath, initial, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            var failed = initial with
            {
                Files = files,
                TotalBytes = totalBytes,
                Status = SnapshotStatus.Incomplete,
                Note = $"快照创建失败：{exception.Message}",
            };
            await WriteManifestAsync(manifestPath, failed, CancellationToken.None).ConfigureAwait(false);
            return Result<SnapshotManifest>.Fail(
                "snapshot.create_failed",
                "快照创建失败；已保留不完整清单，不会被标记为可恢复。",
                exception.Message);
        }
    }

    public async Task<Result<IReadOnlyList<SnapshotManifest>>> ListAsync(
        string instanceId,
        CancellationToken cancellationToken = default)
    {
        if (!AppPaths.IsValidInstanceId(instanceId))
        {
            return Result<IReadOnlyList<SnapshotManifest>>.Fail("snapshot.invalid_instance", "实例 ID 不合法。");
        }

        var directory = Path.Combine(SnapshotsRoot, instanceId);
        if (!Directory.Exists(directory))
        {
            return Result<IReadOnlyList<SnapshotManifest>>.Ok([]);
        }

        var manifests = new List<SnapshotManifest>();
        foreach (var path in Directory.EnumerateFiles(directory, "manifest.json", SearchOption.AllDirectories))
        {
            var manifest = await TryReadManifestAsync(path, cancellationToken).ConfigureAwait(false);
            if (manifest is not null)
            {
                manifests.Add(manifest);
            }
        }

        return Result<IReadOnlyList<SnapshotManifest>>.Ok(
            manifests.OrderByDescending(item => item.CreatedAt).ToArray());
    }

    public async Task<Result<SnapshotManifest>> GetAsync(
        string instanceId,
        string snapshotId,
        CancellationToken cancellationToken = default)
    {
        if (!AppPaths.IsValidInstanceId(instanceId) || !IsValidSnapshotId(snapshotId))
        {
            return Result<SnapshotManifest>.Fail("snapshot.invalid_id", "实例或快照 ID 不合法。");
        }

        var path = Path.Combine(GetSnapshotDirectory(instanceId, snapshotId), "manifest.json");
        if (!File.Exists(path))
        {
            return Result<SnapshotManifest>.Fail("snapshot.not_found", "快照不存在。");
        }

        var manifest = await TryReadManifestAsync(path, cancellationToken).ConfigureAwait(false);
        return manifest is null
            ? Result<SnapshotManifest>.Fail("snapshot.manifest_corrupt", "快照清单损坏。")
            : Result<SnapshotManifest>.Ok(manifest);
    }

    public async Task<Result<SnapshotVerification>> VerifyAsync(
        string instanceId,
        string snapshotId,
        CancellationToken cancellationToken = default)
    {
        var loaded = await GetAsync(instanceId, snapshotId, cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<SnapshotVerification>.Fail(loaded.Error!);
        }

        var manifest = loaded.Value!;
        var problems = new List<string>();
        var missing = 0;
        var corrupt = 0;
        var verified = 0;
        if (manifest.Status != SnapshotStatus.Complete)
        {
            problems.Add($"清单状态为 {manifest.Status}，不是完整快照。");
        }

        foreach (var file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Objects.Verify(file.Sha256, file.Size, out var error))
            {
                if (error?.Contains("缺失", StringComparison.Ordinal) == true)
                {
                    missing++;
                }
                else
                {
                    corrupt++;
                }

                problems.Add($"{file.RelativePath}：{error}");
            }
            else
            {
                verified++;
            }
        }

        return Result<SnapshotVerification>.Ok(new SnapshotVerification(
            snapshotId,
            manifest.Status == SnapshotStatus.Complete && problems.Count == 0,
            missing,
            corrupt,
            verified,
            problems.Take(50).ToArray()));
    }

    public async Task<Result<SnapshotDiffSummary>> DiffAsync(
        string instanceId,
        string snapshotId,
        CancellationToken cancellationToken = default)
    {
        var loaded = await GetAsync(instanceId, snapshotId, cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<SnapshotDiffSummary>.Fail(loaded.Error!);
        }

        var manifest = loaded.Value!;
        var instance = await _instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return Result<SnapshotDiffSummary>.Fail("instance.not_found", "实例不存在。");
        }

        var gameDirectory = InstancePaths.GetGameDirectory(_paths, instance);
        var warnings = new List<string>();
        var currentFiles = EnumerateScopeFiles(gameDirectory, manifest.IncludeRoots, manifest.ExcludeRoots, warnings, cancellationToken);
        var current = new Dictionary<string, (long Size, string? Hash)>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in currentFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                current[entry.RelativePath] = (entry.Size, SnapshotObjectStore.ComputeHash(entry.FullPath));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                current[entry.RelativePath] = (entry.Size, null);
            }
        }

        var snapshot = manifest.Files.ToDictionary(item => item.RelativePath, StringComparer.OrdinalIgnoreCase);
        var entries = new List<SnapshotDiffEntry>();
        var added = 0;
        var removed = 0;
        var modified = 0;
        var unchanged = 0;
        var unknown = 0;

        foreach (var pair in current)
        {
            if (!snapshot.TryGetValue(pair.Key, out var file))
            {
                added++;
                entries.Add(new SnapshotDiffEntry(pair.Key, SnapshotDiffStatus.Added, null, pair.Value.Size));
                continue;
            }

            if (pair.Value.Hash is null)
            {
                unknown++;
                entries.Add(new SnapshotDiffEntry(
                    pair.Key,
                    SnapshotDiffStatus.Unknown,
                    file.Size,
                    pair.Value.Size,
                    "当前文件无法读取，不能判断是否变化。"));
                continue;
            }

            if (string.Equals(pair.Value.Hash, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                unchanged++;
                entries.Add(new SnapshotDiffEntry(pair.Key, SnapshotDiffStatus.Unchanged, file.Size, pair.Value.Size));
            }
            else
            {
                modified++;
                entries.Add(new SnapshotDiffEntry(pair.Key, SnapshotDiffStatus.Modified, file.Size, pair.Value.Size));
            }
        }

        foreach (var file in manifest.Files.Where(file => !current.ContainsKey(file.RelativePath)))
        {
            removed++;
            entries.Add(new SnapshotDiffEntry(file.RelativePath, SnapshotDiffStatus.Removed, file.Size, null));
        }

        return Result<SnapshotDiffSummary>.Ok(new SnapshotDiffSummary(
            snapshotId,
            added,
            removed,
            modified,
            unchanged,
            unknown,
            entries.OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray()));
    }

    public async Task<Result<SnapshotRestorePreview>> PreviewRestoreAsync(
        string instanceId,
        string snapshotId,
        CancellationToken cancellationToken = default)
    {
        var loaded = await GetAsync(instanceId, snapshotId, cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<SnapshotRestorePreview>.Fail(loaded.Error!);
        }

        var manifest = loaded.Value!;
        var instance = await _instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return Result<SnapshotRestorePreview>.Fail("instance.not_found", "实例不存在。");
        }

        var currentDirectory = InstancePaths.GetGameDirectory(_paths, instance);
        var (ownership, sharedWith) = await DescribeOwnershipAsync(instance, currentDirectory, cancellationToken)
            .ConfigureAwait(false);
        var verification = await VerifyAsync(instanceId, snapshotId, cancellationToken).ConfigureAwait(false);
        var diff = await DiffAsync(instanceId, snapshotId, cancellationToken).ConfigureAwait(false);
        var activity = await _transactions.GetActivityStateAsync(instanceId, cancellationToken).ConfigureAwait(false);
        var running = IsInstanceRunning(instanceId);

        var blockers = new List<string>();
        var warnings = new List<string>();
        if (!verification.IsSuccess || !verification.Value!.IsValid)
        {
            blockers.Add("snapshot.incomplete");
        }

        if (string.CompareOrdinal(manifest.SchemaVersion, InstanceEngineVersions.SnapshotSchema) > 0)
        {
            blockers.Add("snapshot.format_unsupported");
        }

        if (running)
        {
            blockers.Add("snapshot.instance_running");
        }

        if (activity.IsSuccess && activity.Value is
            InstanceActivityState.NeedsRecovery or
            InstanceActivityState.Recovering or
            InstanceActivityState.Writing or
            InstanceActivityState.Changing)
        {
            blockers.Add("snapshot.conflict_transaction");
        }

        if (string.Equals(ownership, "SharedExternal", StringComparison.Ordinal))
        {
            blockers.Add("snapshot.shared_target_requires_confirmation");
            warnings.Add("该游戏目录被多个实例共享；恢复会影响其他实例。");
        }
        else if (string.Equals(ownership, "External", StringComparison.Ordinal))
        {
            warnings.Add("该实例的游戏目录位于外部目录；恢复会修改外部数据。");
        }

        var filesToWrite = 0;
        var filesToSkip = 0;
        var filesToDelete = 0;
        long requiredBytes = 0;
        if (diff.IsSuccess)
        {
            foreach (var entry in diff.Value!.Entries)
            {
                switch (entry.Status)
                {
                    case SnapshotDiffStatus.Unchanged:
                        filesToSkip++;
                        break;
                    case SnapshotDiffStatus.Added:
                        filesToDelete++;
                        break;
                    default:
                        filesToWrite++;
                        var file = manifest.Files.FirstOrDefault(item =>
                            string.Equals(item.RelativePath, entry.RelativePath, StringComparison.OrdinalIgnoreCase));
                        requiredBytes += file?.Size ?? 0;
                        break;
                }
            }
        }

        var freeBytes = TryGetFreeBytes(currentDirectory);
        if (freeBytes is { } free && requiredBytes > 0 && free < requiredBytes + 16L * 1024 * 1024)
        {
            blockers.Add("snapshot.insufficient_space");
        }

        var issued = _time.GetUtcNow();
        var planHash = ComputeRestorePlanHash(snapshotId, instanceId, manifest.CreatedAt, currentDirectory);
        var preview = new SnapshotRestorePreview(
            snapshotId,
            instanceId,
            planHash,
            issued,
            issued.AddMinutes(10),
            manifest,
            manifest.GameDirectory,
            currentDirectory,
            ownership,
            sharedWith,
            diff.IsSuccess ? diff.Value! : new SnapshotDiffSummary(snapshotId, 0, 0, 0, 0, 0, []),
            filesToWrite,
            filesToSkip,
            filesToDelete,
            requiredBytes,
            freeBytes,
            running,
            activity.IsSuccess && activity.Value != InstanceActivityState.Idle,
            string.Equals(ownership, "SharedExternal", StringComparison.Ordinal),
            blockers,
            warnings);
        return Result<SnapshotRestorePreview>.Ok(preview);
    }

    public async Task<Result<SnapshotRestoreResult>> RestoreAsync(
        string instanceId,
        string snapshotId,
        SnapshotRestoreOptions? options = null,
        IProgress<SnapshotProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new SnapshotRestoreOptions();
        var previewResult = await PreviewRestoreAsync(instanceId, snapshotId, cancellationToken)
            .ConfigureAwait(false);
        if (!previewResult.IsSuccess)
        {
            return Result<SnapshotRestoreResult>.Fail(previewResult.Error!);
        }

        var preview = previewResult.Value!;
        if (!string.IsNullOrWhiteSpace(options.PreviewHash) &&
            !string.Equals(options.PreviewHash, preview.PlanHash, StringComparison.OrdinalIgnoreCase))
        {
            return Result<SnapshotRestoreResult>.Fail(
                "snapshot.preview_stale",
                "恢复预览已过期或目标已变化，请重新检查。");
        }

        if (preview.Blockers.Any(blocker => blocker == "snapshot.incomplete"))
        {
            return Result<SnapshotRestoreResult>.Fail(
                "snapshot.incomplete",
                "快照不完整、对象损坏或一致性不足，拒绝恢复。");
        }

        if (preview.Blockers.Any(blocker => blocker == "snapshot.format_unsupported"))
        {
            return Result<SnapshotRestoreResult>.Fail(
                "snapshot.format_unsupported",
                "快照格式版本高于当前 ECL，禁止恢复。");
        }

        if (preview.IsInstanceRunning && !options.AllowRunningInstance)
        {
            return Result<SnapshotRestoreResult>.Fail(
                "snapshot.instance_running",
                "实例正在运行，不能恢复快照；请先关闭游戏。");
        }

        if (preview.HasPendingTransaction)
        {
            return Result<SnapshotRestoreResult>.Fail(
                "snapshot.conflict_transaction",
                "实例存在未完成事务或正在变更，请先处理后再恢复。");
        }

        if (preview.IsSharedTarget && !options.AllowSharedTarget)
        {
            return Result<SnapshotRestoreResult>.Fail(
                "snapshot.shared_target_requires_confirmation",
                "该游戏目录被多个实例共享，恢复会影响其他实例；需要显式确认。");
        }

        if (string.Equals(preview.Ownership, "External", StringComparison.Ordinal) &&
            !options.AllowExternalWrite &&
            !options.AllowSharedTarget)
        {
            return Result<SnapshotRestoreResult>.Fail(
                "snapshot.external_target_requires_confirmation",
                "恢复会修改外部游戏目录；需要显式确认。");
        }

        if (preview.Blockers.Any(blocker => blocker == "snapshot.insufficient_space"))
        {
            return Result<SnapshotRestoreResult>.Fail(
                "snapshot.insufficient_space",
                "目标磁盘空间不足，无法安全恢复。");
        }

        var instance = await _instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return Result<SnapshotRestoreResult>.Fail("instance.not_found", "实例不存在。");
        }

        var gameDirectory = InstancePaths.GetGameDirectory(_paths, instance);
        string? safetySnapshotId = null;
        if (options.CreateSafetySnapshot)
        {
            var safety = await CreateAsync(
                instanceId,
                new SnapshotCreateOptions(
                    preview.Manifest.Scope,
                    preview.Manifest.WorldName,
                    AllowRunningInstance: true,
                    Origin: SnapshotOrigin.Safety,
                    RetentionClass: SnapshotRetentionClass.Automatic),
                progress,
                cancellationToken).ConfigureAwait(false);
            if (!safety.IsSuccess)
            {
                return Result<SnapshotRestoreResult>.Fail(
                    "snapshot.safety_failed",
                    "无法创建恢复前快照，已中止恢复。",
                    safety.Error?.Message);
            }

            safetySnapshotId = safety.Value!.SnapshotId;
        }

        var warnings = new List<string>(preview.Warnings);
        var currentFiles = EnumerateScopeFiles(
            gameDirectory,
            preview.Manifest.IncludeRoots,
            preview.Manifest.ExcludeRoots,
            warnings,
            cancellationToken);
        var currentByPath = currentFiles.ToDictionary(
            item => item.RelativePath,
            item => item,
            StringComparer.OrdinalIgnoreCase);
        var operations = new List<InstanceChangeOperation>();
        var unchanged = 0;
        foreach (var file in preview.Manifest.Files)
        {
            var objectPath = Objects.GetObjectPath(file.Sha256);
            if (!File.Exists(objectPath))
            {
                return Result<SnapshotRestoreResult>.Fail("snapshot.object_missing", "快照对象缺失，无法恢复。", file.RelativePath);
            }

            if (currentByPath.TryGetValue(file.RelativePath, out var current) &&
                current.Size == file.Size &&
                string.Equals(
                    SnapshotObjectStore.ComputeHash(current.FullPath),
                    file.Sha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                unchanged++;
                continue;
            }

            operations.Add(InstanceChangeOperation.CopyFrom(
                file.RelativePath,
                objectPath,
                file.Sha256));
        }

        var extraFiles = options.DeleteExtraFiles
            ? currentByPath.Keys
                .Except(preview.Manifest.Files.Select(item => item.RelativePath), StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];
        foreach (var extra in extraFiles)
        {
            operations.Add(InstanceChangeOperation.Delete(extra));
        }

        if (operations.Count == 0)
        {
            return Result<SnapshotRestoreResult>.Ok(new SnapshotRestoreResult(
                snapshotId,
                string.Empty,
                0,
                0,
                unchanged,
                safetySnapshotId,
                warnings));
        }

        var transaction = await _transactions.ExecuteAsync(
            instanceId,
            new InstanceChangePlan(
                operations,
                $"恢复快照 {snapshotId}",
                Kind: InstanceTransactionKind.SnapshotRestore),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!transaction.IsSuccess)
        {
            return Result<SnapshotRestoreResult>.Fail(transaction.Error!);
        }

        return Result<SnapshotRestoreResult>.Ok(new SnapshotRestoreResult(
            snapshotId,
            transaction.Value!.TransactionId,
            operations.Count - extraFiles.Length,
            extraFiles.Length,
            unchanged,
            safetySnapshotId,
            warnings));
    }

    public Task<Result> DeleteAsync(
        string instanceId,
        string snapshotId,
        CancellationToken cancellationToken = default)
    {
        if (!AppPaths.IsValidInstanceId(instanceId) || !IsValidSnapshotId(snapshotId))
        {
            return Task.FromResult(Result.Fail("snapshot.invalid_id", "实例或快照 ID 不合法。"));
        }

        var directory = GetSnapshotDirectory(instanceId, snapshotId);
        if (!Directory.Exists(directory))
        {
            return Task.FromResult(Result.Fail("snapshot.not_found", "快照不存在。"));
        }

        try
        {
            Directory.Delete(directory, recursive: true);
            return Task.FromResult(Result.Ok());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(Result.Fail("snapshot.delete_failed", "删除快照失败。", exception.Message));
        }
    }

    public async Task<Result<IReadOnlyList<SnapshotGroupSummary>>> ListGroupsAsync(
        CancellationToken cancellationToken = default)
    {
        var groups = new List<SnapshotGroupSummary>();
        if (!Directory.Exists(SnapshotsRoot))
        {
            return Result<IReadOnlyList<SnapshotGroupSummary>>.Ok(groups);
        }

        foreach (var directory in Directory.EnumerateDirectories(SnapshotsRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var instanceId = Path.GetFileName(directory);
            if (string.Equals(instanceId, "objects", StringComparison.OrdinalIgnoreCase) ||
                !AppPaths.IsValidInstanceId(instanceId))
            {
                continue;
            }

            var manifests = new List<SnapshotManifest>();
            foreach (var path in Directory.EnumerateFiles(directory, "manifest.json", SearchOption.AllDirectories))
            {
                var manifest = await TryReadManifestAsync(path, cancellationToken).ConfigureAwait(false);
                if (manifest is not null)
                {
                    manifests.Add(manifest);
                }
            }

            var instance = await _instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
            groups.Add(new SnapshotGroupSummary(
                instanceId,
                instance?.Name,
                manifests.Count,
                manifests.Sum(item => item.TotalBytes),
                manifests.Count == 0 ? null : manifests.Min(item => item.CreatedAt),
                manifests.Count == 0 ? null : manifests.Max(item => item.CreatedAt),
                manifests.Count(item => item.Status == SnapshotStatus.Incomplete),
                manifests.Count(item => item.Status == SnapshotStatus.Corrupt),
                instance is null,
                manifests.Select(item => item.SnapshotId).OrderByDescending(item => item).ToArray()));
        }

        return Result<IReadOnlyList<SnapshotGroupSummary>>.Ok(
            groups.OrderBy(item => item.Orphaned).ThenBy(item => item.InstanceId).ToArray());
    }

    public async Task<Result<SnapshotGroupSummary>> DeleteInstanceSnapshotsAsync(
        string instanceId,
        CancellationToken cancellationToken = default)
    {
        if (!AppPaths.IsValidInstanceId(instanceId))
        {
            return Result<SnapshotGroupSummary>.Fail("snapshot.invalid_instance", "实例 ID 不合法。");
        }

        var groups = await ListGroupsAsync(cancellationToken).ConfigureAwait(false);
        if (!groups.IsSuccess)
        {
            return Result<SnapshotGroupSummary>.Fail(groups.Error!);
        }

        var group = groups.Value!.FirstOrDefault(item =>
            string.Equals(item.InstanceId, instanceId, StringComparison.Ordinal));
        if (group is null)
        {
            return Result<SnapshotGroupSummary>.Fail("snapshot.not_found", "该实例没有快照。");
        }

        try
        {
            var directory = Path.Combine(SnapshotsRoot, instanceId);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }

            return Result<SnapshotGroupSummary>.Ok(group);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result<SnapshotGroupSummary>.Fail(
                "snapshot.delete_failed",
                "删除实例快照清单失败。",
                exception.Message);
        }
    }

    public async Task<Result<SnapshotPruneResult>> PruneOrphansAsync(
        SnapshotPruneOptions options,
        CancellationToken cancellationToken = default)
    {
        options ??= new SnapshotPruneOptions();
        var referenced = await CollectReferencedHashesAsync(cancellationToken).ConfigureAwait(false);
        var grace = options.GracePeriod ?? TimeSpan.FromHours(1);
        var cutoff = _time.GetUtcNow() - grace;
        long freed = 0;
        var deleted = 0;
        var retained = 0;
        var notes = new List<string>();
        var objects = Objects;
        foreach (var hash in objects.EnumerateObjectHashes().ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (referenced.Contains(hash))
            {
                retained++;
                continue;
            }

            var path = objects.GetObjectPath(hash);
            long length;
            DateTime lastWrite;
            try
            {
                var info = new FileInfo(path);
                length = info.Length;
                lastWrite = info.LastWriteTimeUtc;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                notes.Add($"对象不可读：{hash}");
                retained++;
                continue;
            }

            if (lastWrite > cutoff.UtcDateTime)
            {
                retained++;
                continue;
            }

            if (options.DryRun)
            {
                freed += length;
                notes.Add($"可回收：{hash}（{length} 字节）");
                deleted++;
                continue;
            }

            try
            {
                objects.DeleteObject(hash);
                freed += length;
                deleted++;
                if (options.MaxBytes is { } max && freed >= max)
                {
                    notes.Add("已达到本次回收上限。");
                    break;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                notes.Add($"删除对象失败：{hash}（{exception.Message}）");
                retained++;
            }
        }

        return Result<SnapshotPruneResult>.Ok(new SnapshotPruneResult(freed, deleted, retained, notes.Take(100).ToArray()));
    }

    public async Task<Result<SnapshotStorageSummary>> GetStorageSummaryAsync(
        CancellationToken cancellationToken = default)
    {
        var groups = await ListGroupsAsync(cancellationToken).ConfigureAwait(false);
        if (!groups.IsSuccess)
        {
            return Result<SnapshotStorageSummary>.Fail(groups.Error!);
        }

        long manifestBytes = 0;
        if (Directory.Exists(SnapshotsRoot))
        {
            foreach (var path in Directory.EnumerateFiles(SnapshotsRoot, "manifest.json", SearchOption.AllDirectories))
            {
                try
                {
                    manifestBytes += new FileInfo(path).Length;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // 不可读清单不计入。
                }
            }
        }

        var referenced = await CollectReferencedHashesAsync(cancellationToken).ConfigureAwait(false);
        long objectBytes = 0;
        var orphanObjects = 0;
        foreach (var hash in Objects.EnumerateObjectHashes().ToArray())
        {
            long length;
            try
            {
                length = Objects.GetObjectSize(hash);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            objectBytes += length;
            if (!referenced.Contains(hash))
            {
                orphanObjects++;
            }
        }

        return Result<SnapshotStorageSummary>.Ok(new SnapshotStorageSummary(
            groups.Value!.Count,
            groups.Value.Sum(item => item.SnapshotCount),
            objectBytes,
            manifestBytes,
            groups.Value.Count(item => item.Orphaned),
            orphanObjects));
    }

    private async Task<HashSet<string>> CollectReferencedHashesAsync(CancellationToken cancellationToken)
    {
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(SnapshotsRoot))
        {
            return referenced;
        }

        foreach (var path in Directory.EnumerateFiles(SnapshotsRoot, "manifest.json", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifest = await TryReadManifestAsync(path, cancellationToken).ConfigureAwait(false);
            if (manifest is null)
            {
                continue;
            }

            foreach (var file in manifest.Files)
            {
                referenced.Add(file.Sha256);
            }
        }

        return referenced;
    }

    public async Task<Result<long>> PruneOrphansAsync(CancellationToken cancellationToken = default) =>
        (await PruneOrphansAsync(new SnapshotPruneOptions(), cancellationToken).ConfigureAwait(false)) is { IsSuccess: true } result
            ? Result<long>.Ok(result.Value!.FreedBytes)
            : Result<long>.Fail("snapshot.prune_failed", "清理孤立快照对象失败。");

    private async Task<(string Ownership, IReadOnlyList<string> SharedWith)> DescribeOwnershipAsync(
        GameInstance instance,
        string gameDirectory,
        CancellationToken cancellationToken)
    {
        if (instance.GameDirectory is null)
        {
            return ("EclManaged", []);
        }

        var all = await _instances.GetInstancesAsync(cancellationToken).ConfigureAwait(false);
        var shared = all
            .Where(other => !string.Equals(other.Id, instance.Id, StringComparison.Ordinal))
            .Where(other =>
            {
                try
                {
                    return string.Equals(
                        InstancePaths.GetGameDirectory(_paths, other),
                        gameDirectory,
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
                }
                catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
                {
                    return false;
                }
            })
            .Select(other => other.Id)
            .ToArray();
        return shared.Length > 0 ? ("SharedExternal", shared) : ("External", []);
    }

    private static long? TryGetFreeBytes(string directory)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(directory));
            if (string.IsNullOrWhiteSpace(root))
            {
                return null;
            }

            var drive = new DriveInfo(root);
            return drive.IsReady ? drive.AvailableFreeSpace : null;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string ComputeRestorePlanHash(
        string snapshotId,
        string instanceId,
        DateTimeOffset createdAt,
        string currentDirectory)
    {
        var text = $"{snapshotId}|{instanceId}|{createdAt:O}|{currentDirectory}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private bool IsInstanceRunning(string instanceId)
    {
        foreach (var session in _sessions.List())
        {
            var manifest = session.Manifest;
            if (!string.Equals(manifest.InstanceId, instanceId, StringComparison.Ordinal))
            {
                continue;
            }

            if (manifest.State is not (
                GameSessionState.Preparing or
                GameSessionState.Starting or
                GameSessionState.Running))
            {
                continue;
            }

            if (manifest.ProcessId is not { } processId || processId <= 0)
            {
                return true;
            }

            try
            {
                using var process = Process.GetProcessById(processId);
                if (!process.HasExited)
                {
                    return true;
                }
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                // 进程已退出：会话状态尚未收尾，不视为运行中。
            }
        }

        return false;
    }

    private (IReadOnlyList<string> Roots, IReadOnlyList<string> Excludes, string? WorldName) ResolveScope(
        string gameDirectory,
        SnapshotCreateOptions options)
    {
        switch (options.Scope)
        {
            case SnapshotScope.World:
                {
                    var world = options.WorldName?.Trim();
                    if (string.IsNullOrWhiteSpace(world) ||
                        world is "." or ".." ||
                        world.Contains('/') || world.Contains('\\') || world.Contains(':'))
                    {
                        throw new InvalidOperationException("世界名称不合法。");
                    }

                    if (!Directory.Exists(Path.Combine(gameDirectory, "saves", world)))
                    {
                        throw new InvalidOperationException("世界存档不存在。");
                    }

                    return ([$"saves/{world}"], ["session.lock"], world);
                }
            case SnapshotScope.Environment:
                {
                    var roots = new List<string>();
                    foreach (var name in new[]
                             {
                                 "mods", "config", "defaultconfigs", "resourcepacks", "shaderpacks", "kubejs",
                                 "options.txt", "servers.dat",
                             })
                    {
                        if (File.Exists(Path.Combine(gameDirectory, name)) ||
                            Directory.Exists(Path.Combine(gameDirectory, name)))
                        {
                            roots.Add(name);
                        }
                    }

                    return (roots, [], null);
                }
            default:
                return ([""], FullExcludedRoots, null);
        }
    }

    private static List<SnapshotEntry> EnumerateScopeFiles(
        string gameDirectory,
        IReadOnlyList<string> roots,
        IReadOnlyList<string> excludes,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var entries = new List<SnapshotEntry>();
        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullRoot = root.Length == 0
                ? gameDirectory
                : Path.Combine(gameDirectory, root.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(fullRoot))
            {
                AddFile(fullRoot, gameDirectory, excludes, entries);
                continue;
            }

            if (!Directory.Exists(fullRoot))
            {
                continue;
            }

            var pending = new Stack<string>();
            pending.Push(fullRoot);
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directory = pending.Pop();
                IEnumerable<string> children;
                try
                {
                    children = Directory.EnumerateFileSystemEntries(directory);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    warnings.Add($"无法枚举 {directory}：{exception.Message}");
                    continue;
                }

                foreach (var child in children)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var relative = Path.GetRelativePath(gameDirectory, child);
                    if (!IsExcluded(relative, excludes))
                    {
                        continue;
                    }

                    FileAttributes attributes;
                    try
                    {
                        attributes = File.GetAttributes(child);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        warnings.Add($"无法读取 {relative} 的属性：{exception.Message}");
                        continue;
                    }

                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        warnings.Add($"跳过链接：{relative}");
                        continue;
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        pending.Push(child);
                    }
                    else
                    {
                        AddFile(child, gameDirectory, excludes, entries);
                        if (entries.Count > MaxFiles)
                        {
                            throw new IOException($"快照文件数量超过 {MaxFiles} 上限。");
                        }
                    }
                }
            }
        }

        return entries
            .GroupBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void AddFile(
        string fullPath,
        string gameDirectory,
        IReadOnlyList<string> excludes,
        List<SnapshotEntry> entries)
    {
        var relative = Path.GetRelativePath(gameDirectory, fullPath);
        if (!IsExcluded(relative, excludes))
        {
            return;
        }

        if (fullPath.EndsWith(".lock", StringComparison.OrdinalIgnoreCase) ||
            fullPath.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var info = new FileInfo(fullPath);
        entries.Add(new SnapshotEntry(
            relative.Replace('\\', '/'),
            fullPath,
            info.Length,
            info.LastWriteTimeUtc));
    }

    /// <summary>返回 true 表示路径可以包含。</summary>
    private static bool IsExcluded(string relative, IReadOnlyList<string> excludes)
    {
        var normalized = relative.Replace('\\', '/');
        foreach (var exclude in excludes)
        {
            if (normalized.Equals(exclude, StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith(exclude + "/", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private async Task WriteManifestAsync(
        string path,
        SnapshotManifest manifest,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporary,
                JsonSerializer.Serialize(manifest, EclJson.Indented),
                cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // 残留临时文件无害。
                }
            }
        }
    }

    private static async Task<SnapshotManifest?> TryReadManifestAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<SnapshotManifest>(json, EclJson.Default);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private string GetSnapshotDirectory(string instanceId, string snapshotId) =>
        Path.Combine(SnapshotsRoot, instanceId, snapshotId);

    private static bool IsValidSnapshotId(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= MaxSnapshotIdLength &&
        value.All(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.');

    private static string CreateSnapshotId() =>
        $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{RandomNumberGenerator.GetHexString(6).ToLowerInvariant()}";

    private sealed record SnapshotEntry(
        string RelativePath,
        string FullPath,
        long Size,
        DateTime ModifiedAt);
}
