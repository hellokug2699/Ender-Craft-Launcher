// SPDX-License-Identifier: GPL-3.0-or-later

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ecl.Core;
using Ecl.Core.Json;
using Ecl.Minecraft.Engine.Transactions;
using Ecl.Minecraft.Instances;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ecl.Minecraft.Engine.Snapshots;

/// <summary>
/// 用户快照与恢复（ADR 0048）。快照与事务恢复材料使用不同目录和生命周期：
/// 本服务只管理用户快照；事务备份由 <see cref="InstanceTransactionCoordinator"/> 管理。
/// </summary>
public sealed class InstanceSnapshotService : IInstanceSnapshotService
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
    ];

    private readonly IInstanceService _instances;
    private readonly InstanceTransactionCoordinator _transactions;
    private readonly IAppPaths _paths;
    private readonly ILogger<InstanceSnapshotService> _logger;
    private readonly TimeProvider _time;

    public InstanceSnapshotService(
        IInstanceService instances,
        InstanceTransactionCoordinator transactions,
        IAppPaths paths,
        ILogger<InstanceSnapshotService>? logger = null,
        TimeProvider? time = null)
    {
        _instances = instances;
        _transactions = transactions;
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
        var consistencyWarning = (string?)null;
        if (options.Scope == SnapshotScope.World && worldName is not null)
        {
            var sessionLock = Path.Combine(gameDirectory, "saves", worldName, "session.lock");
            if (File.Exists(sessionLock))
            {
                if (!options.AllowRunningWorld)
                {
                    return Result<SnapshotManifest>.Fail(
                        "snapshot.world_running",
                        "世界似乎仍被游戏占用（存在 session.lock）；请关闭游戏后重试。",
                        worldName);
                }

                consistencyWarning = "世界在游戏运行期间创建快照，跨文件状态可能不一致。";
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
            consistencyWarning);
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

            var manifest = initial with
            {
                Files = files,
                TotalBytes = totalBytes,
                Status = incomplete ? SnapshotStatus.Incomplete : SnapshotStatus.Complete,
                Note = incomplete
                    ? $"快照不完整：{warnings.Count} 个文件无法读取。"
                    : null,
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

    public async Task<Result<SnapshotRestoreResult>> RestoreAsync(
        string instanceId,
        string snapshotId,
        SnapshotRestoreOptions? options = null,
        IProgress<SnapshotProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new SnapshotRestoreOptions();
        var verification = await VerifyAsync(instanceId, snapshotId, cancellationToken).ConfigureAwait(false);
        if (!verification.IsSuccess)
        {
            return Result<SnapshotRestoreResult>.Fail(verification.Error!);
        }

        if (!verification.Value!.IsValid)
        {
            return Result<SnapshotRestoreResult>.Fail(
                "snapshot.incomplete",
                "快照不完整或对象损坏，拒绝恢复。",
                string.Join("；", verification.Value.Problems.Take(5)));
        }

        var loaded = await GetAsync(instanceId, snapshotId, cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<SnapshotRestoreResult>.Fail(loaded.Error!);
        }

        var manifest = loaded.Value!;
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
                    manifest.Scope,
                    manifest.WorldName,
                    AllowRunningWorld: true),
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

        var warnings = new List<string>();
        var currentFiles = EnumerateScopeFiles(
            gameDirectory,
            manifest.IncludeRoots,
            manifest.ExcludeRoots,
            warnings,
            cancellationToken);
        var current = currentFiles
            .Select(item => item.RelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var operations = new List<InstanceChangeOperation>();
        foreach (var file in manifest.Files)
        {
            var objectPath = Objects.GetObjectPath(file.Sha256);
            if (!File.Exists(objectPath))
            {
                return Result<SnapshotRestoreResult>.Fail("snapshot.object_missing", "快照对象缺失，无法恢复。", file.RelativePath);
            }

            operations.Add(InstanceChangeOperation.CopyFrom(
                file.RelativePath,
                objectPath,
                file.Sha256));
        }

        var extraFiles = options.DeleteExtraFiles
            ? current.Except(manifest.Files.Select(item => item.RelativePath), StringComparer.OrdinalIgnoreCase).ToArray()
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
                manifest.Files.Count,
                safetySnapshotId,
                warnings));
        }

        var transaction = await _transactions.ExecuteAsync(
            instanceId,
            new InstanceChangePlan(operations, $"恢复快照 {snapshotId}"),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!transaction.IsSuccess)
        {
            return Result<SnapshotRestoreResult>.Fail(transaction.Error!);
        }

        return Result<SnapshotRestoreResult>.Ok(new SnapshotRestoreResult(
            snapshotId,
            transaction.Value!.TransactionId,
            manifest.Files.Count,
            extraFiles.Length,
            0,
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

    public async Task<Result<long>> PruneOrphansAsync(CancellationToken cancellationToken = default)
    {
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(SnapshotsRoot))
        {
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
        }

        long freed = 0;
        var cutoff = _time.GetUtcNow().AddHours(-1);
        foreach (var hash in Objects.EnumerateObjectHashes().ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (referenced.Contains(hash))
            {
                continue;
            }

            var path = Objects.GetObjectPath(hash);
            var info = new FileInfo(path);
            if (info.LastWriteTimeUtc > cutoff.UtcDateTime)
            {
                // 正在创建中的快照可能尚未写入清单，保留近期对象。
                continue;
            }

            try
            {
                freed += info.Length;
                Objects.DeleteObject(hash);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(exception, "删除孤立快照对象失败：{Path}", path);
            }
        }

        return Result<long>.Ok(freed);
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
