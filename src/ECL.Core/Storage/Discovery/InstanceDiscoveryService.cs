// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ecl.Core.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ecl.Core.Storage.Discovery;

/// <summary>
/// F-02 多来源发现：只读扫描来源根目录，把候选写入持久发现目录。
/// 扫描绝不写来源目录（无探针/锁/临时文件/标记），单根失败不影响其他根，
/// 部分结果不得声称完整。
/// </summary>
public sealed class InstanceDiscoveryService : IInstanceDiscoveryService
{
    private static readonly string[] IsolationMarkers =
    [
        "mods",
        "config",
        "saves",
        "options.txt",
        "servers.dat",
        "resourcepacks",
        "shaderpacks",
    ];

    private static readonly string[] ExcludedDirectoryNames =
    [
        "libraries",
        "assets",
        "logs",
        "crash-reports",
        "natives",
    ];

    private const int MaxManifestBytes = 4 * 1024 * 1024;
    private const int MaxInstanceMetadataBytes = 1024 * 1024;
    private const int MaxMarkerBytes = 16 * 1024;
    private const int MaxJsonDepth = 16;

    private readonly IWorkspaceService _workspace;
    private readonly IAppPaths _paths;
    private readonly DiscoveryCatalogStore _store;
    private readonly ILogger<InstanceDiscoveryService> _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _rootGates = new(StringComparer.Ordinal);

    public InstanceDiscoveryService(
        IWorkspaceService workspace,
        IAppPaths paths,
        ILogger<InstanceDiscoveryService>? logger = null,
        TimeProvider? time = null)
    {
        _workspace = workspace;
        _paths = paths;
        _store = new DiscoveryCatalogStore(paths.Root);
        _logger = logger ?? NullLogger<InstanceDiscoveryService>.Instance;
        _time = time ?? TimeProvider.System;
    }

    public async Task<Result<DiscoveryScanReport>> ScanAsync(
        DiscoveryScanRequest? request = null,
        IProgress<DiscoveryProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        request ??= new DiscoveryScanRequest();
        var gateAcquired = false;
        try
        {
            try
            {
                gateAcquired = await _scanGate.WaitAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return Result<DiscoveryScanReport>.Fail("source.cancelled", "来源扫描已取消，未提交本次结果。");
            }

            if (!gateAcquired)
            {
                return Result<DiscoveryScanReport>.Fail(
                    "source.scan_in_progress",
                    "已有来源扫描正在进行，请稍后重试。");
            }

            var started = _time.GetUtcNow();
            var warnings = new List<string>();
            var changes = new List<DiscoveryChange>();
            var reports = new List<DiscoveryRootReport>();
            var candidates = new Dictionary<string, List<DiscoveryEntry>>(StringComparer.Ordinal);
            var rootStates = new Dictionary<string, DiscoveryRootState>(StringComparer.Ordinal);
            var scanId = $"scan-{started:yyyyMMddHHmmss}-{RandomNumberGenerator.GetHexString(6).ToLowerInvariant()}";

            var catalogLoad = _store.Load();
            if (catalogLoad.IsUnsupported)
            {
                return Result<DiscoveryScanReport>.Fail(
                    catalogLoad.ErrorCode!,
                    catalogLoad.ErrorMessage!);
            }

            if (catalogLoad.ErrorMessage is not null)
            {
                warnings.Add(catalogLoad.ErrorMessage);
            }

            var existing = catalogLoad.Catalog;
            var roots = ResolveRoots(request);

            foreach (var root in roots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new DiscoveryProgress(root.Id, null, 0, "scanning"));
                var gate = _rootGates.GetOrAdd(root.Id, _ => new SemaphoreSlim(1, 1));
                if (!await gate.WaitAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false))
                {
                    reports.Add(new DiscoveryRootReport(
                        root.Id,
                        root.Status,
                        InstanceRootStructure.Unknown,
                        0,
                        0,
                        0,
                        0,
                        0,
                        0,
                        1,
                        TimedOut: false,
                        Cancelled: false,
                        TimeSpan.Zero,
                        [new DiscoveryError(root.Id, null, "source.scan_in_progress", "同一来源已有扫描正在进行。")]));
                    warnings.Add($"{root.Name}：同一来源已有扫描正在进行。");
                    continue;
                }

                try
                {
                    var scan = await ScanRootAsync(root, request, scanId, progress, cancellationToken)
                        .ConfigureAwait(false);
                    reports.Add(scan.Report);
                    if (scan.Entries.Count > 0)
                    {
                        candidates[root.Id] = scan.Entries.ToList();
                    }

                    rootStates[root.Id] = new DiscoveryRootState(
                        root.Id,
                        root.PathIdentityKey,
                        scanId,
                        scan.Report.IsComplete,
                        _time.GetUtcNow(),
                        scan.Report.Errors.FirstOrDefault()?.Code);

                    foreach (var error in scan.Report.Errors)
                    {
                        warnings.Add($"{root.Name}：{error.Code} {error.Detail}".Trim());
                    }
                }
                finally
                {
                    gate.Release();
                }
            }

            if (cancellationToken.IsCancellationRequested)
            {
                // 取消不提交发现目录：旧目录保留，只记录扫描尝试。
                await UpdateRootAttemptsAsync(roots, "source.cancelled", cancellationToken).ConfigureAwait(false);
                return Result<DiscoveryScanReport>.Fail("source.cancelled", "来源扫描已取消，未提交本次结果。");
            }

            var committed = CommitUnderLock(
                existing,
                roots,
                candidates,
                rootStates,
                scanId,
                changes,
                warnings,
                cancellationToken);
            if (!committed.IsSuccess)
            {
                return Result<DiscoveryScanReport>.Fail(committed.Error!);
            }

            foreach (var report in reports)
            {
                var root = roots.First(item => string.Equals(item.Id, report.RootId, StringComparison.Ordinal));
                await ApplyRootStateAsync(root, report, cancellationToken).ConfigureAwait(false);
            }

            var completed = _time.GetUtcNow();
            var isComplete = reports.Count > 0 && reports.All(report => report.IsComplete);
            if (reports.Count == 0)
            {
                warnings.Add("没有已启用的来源需要扫描。");
            }

            _logger.LogInformation(
                "来源扫描完成：根数={RootCount} 条目={EntryCount} 完整={Complete} 耗时={Duration}",
                reports.Count,
                reports.Sum(report => report.EntriesFound),
                isComplete,
                completed - started);

            return Result<DiscoveryScanReport>.Ok(new DiscoveryScanReport(
                reports,
                changes,
                started,
                completed,
                isComplete,
                warnings));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result<DiscoveryScanReport>.Fail("source.cancelled", "来源扫描已取消，未提交本次结果。");
        }
        finally
        {
            if (gateAcquired)
            {
                _scanGate.Release();
            }
        }
    }

    public async Task<Result<DiscoveryCatalog>> GetCatalogAsync(
        bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var load = _store.Load();
        if (load.IsUnsupported)
        {
            return Result<DiscoveryCatalog>.Fail(load.ErrorCode!, load.ErrorMessage!);
        }

        var catalog = ReconcileUnbound(load.Catalog);
        if (!includeInactive)
        {
            catalog = catalog with
            {
                Entries = catalog.Entries.Where(IsActiveEntry).ToArray(),
            };
        }

        return Result<DiscoveryCatalog>.Ok(catalog);
    }

    public async Task<Result<DiscoveryEntry>> GetEntryAsync(
        string discoveryId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var load = _store.Load();
        if (load.IsUnsupported)
        {
            return Result<DiscoveryEntry>.Fail(load.ErrorCode!, load.ErrorMessage!);
        }

        var entry = load.Catalog.Entries.FirstOrDefault(item =>
            string.Equals(item.DiscoveryId, discoveryId, StringComparison.Ordinal));
        return entry is null
            ? Result<DiscoveryEntry>.Fail("discovery.not_found", "未找到该发现条目。")
            : Result<DiscoveryEntry>.Ok(entry);
    }

    public async Task<Result<DiscoveryEntry>> RefreshEntryAsync(
        string discoveryId,
        CancellationToken cancellationToken = default)
    {
        var entryResult = await GetEntryAsync(discoveryId, cancellationToken).ConfigureAwait(false);
        if (!entryResult.IsSuccess)
        {
            return entryResult;
        }

        var entry = entryResult.Value!;
        var scan = await ScanAsync(
            new DiscoveryScanRequest { RootIds = [entry.RootId] },
            progress: null,
            cancellationToken).ConfigureAwait(false);
        if (!scan.IsSuccess)
        {
            return Result<DiscoveryEntry>.Fail(scan.Error!);
        }

        var refreshed = await GetEntryAsync(discoveryId, cancellationToken).ConfigureAwait(false);
        if (!refreshed.IsSuccess)
        {
            return Result<DiscoveryEntry>.Fail(
                "discovery.entry_missing",
                "刷新后该条目已不存在（来源可能已失效）。");
        }

        return refreshed;
    }

    public Task<Result<DiscoveryEntry>> ResolveDuplicateAsync(
        string discoveryId,
        DuplicateResolution resolution,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var fileLock = CrossProcessFileLock.TryAcquire(
            _paths.Root,
            "discovery.lock",
            TimeSpan.FromSeconds(2),
            cancellationToken);
        var load = _store.Load();
        if (load.IsUnsupported)
        {
            return Task.FromResult(Result<DiscoveryEntry>.Fail(load.ErrorCode!, load.ErrorMessage!));
        }

        var catalog = load.Catalog;
        var entry = catalog.Entries.FirstOrDefault(item =>
            string.Equals(item.DiscoveryId, discoveryId, StringComparison.Ordinal));
        if (entry is null)
        {
            return Task.FromResult(Result<DiscoveryEntry>.Fail("discovery.not_found", "未找到该发现条目。"));
        }

        switch (resolution)
        {
            case DuplicateResolution.KeepSeparate:
                if (entry.DuplicateOfDiscoveryId is not null)
                {
                    var representative = catalog.Entries.FirstOrDefault(item =>
                        string.Equals(item.DiscoveryId, entry.DuplicateOfDiscoveryId, StringComparison.Ordinal));
                    if (representative is not null && SamePhysicalPath(entry, representative))
                    {
                        return Task.FromResult(Result<DiscoveryEntry>.Fail(
                            "discovery.duplicate_same_path",
                            "两个条目指向同一物理目录，不能作为独立实例保留。"));
                    }
                }

                entry = entry with
                {
                    Status = DiscoveryEntryStatus.Active,
                    DuplicateOfDiscoveryId = null,
                    DuplicateGroupId = null,
                    DuplicateResolution = "keep-separate",
                };
                break;

            case DuplicateResolution.UseRepresentative:
            {
                if (string.IsNullOrWhiteSpace(entry.DuplicateGroupId))
                {
                    return Task.FromResult(Result<DiscoveryEntry>.Fail(
                        "discovery.not_duplicate",
                        "该条目不属于任何重复组，无需选择代表。"));
                }

                var representativeId = entry.DuplicateOfDiscoveryId ?? entry.DiscoveryId;
                var group = catalog.Entries
                    .Where(item => string.Equals(item.DuplicateGroupId, entry.DuplicateGroupId, StringComparison.Ordinal))
                    .Where(item => !string.Equals(item.DiscoveryId, representativeId, StringComparison.Ordinal))
                    .Select(item => item with
                    {
                        Status = DiscoveryEntryStatus.PotentialDuplicate,
                        DuplicateOfDiscoveryId = representativeId,
                    })
                    .ToArray();
                catalog = catalog with
                {
                    Entries = catalog.Entries
                        .Select(item => string.Equals(item.DiscoveryId, entry.DiscoveryId, StringComparison.Ordinal) ? entry : item)
                        .Select(item => group.FirstOrDefault(replacement =>
                            string.Equals(replacement.DiscoveryId, item.DiscoveryId, StringComparison.Ordinal)) ?? item)
                        .ToArray(),
                };
                entry = catalog.Entries.First(item => string.Equals(item.DiscoveryId, entry.DiscoveryId, StringComparison.Ordinal));
                break;
            }

            case DuplicateResolution.ForgetDuplicate:
                if (string.IsNullOrWhiteSpace(entry.DuplicateOfDiscoveryId))
                {
                    return Task.FromResult(Result<DiscoveryEntry>.Fail(
                        "discovery.not_duplicate",
                        "该条目不是重复条目；如需移除请使用‘忘记条目’。"));
                }

                catalog = catalog with
                {
                    Entries = catalog.Entries
                        .Where(item => !string.Equals(item.DiscoveryId, entry.DiscoveryId, StringComparison.Ordinal))
                        .ToArray(),
                };
                break;

            default:
                return Task.FromResult(Result<DiscoveryEntry>.Fail("discovery.invalid_resolution", "未知的重复处理方式。"));
        }

        catalog = catalog with { GeneratedAt = _time.GetUtcNow() };
        _store.Save(catalog);
        return Task.FromResult(Result<DiscoveryEntry>.Ok(entry));
    }

    public Task<Result> ForgetEntryAsync(string discoveryId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var fileLock = CrossProcessFileLock.TryAcquire(
            _paths.Root,
            "discovery.lock",
            TimeSpan.FromSeconds(2),
            cancellationToken);
        var load = _store.Load();
        if (load.IsUnsupported)
        {
            return Task.FromResult(Result.Fail(load.ErrorCode!, load.ErrorMessage!));
        }

        var catalog = load.Catalog;
        if (!catalog.Entries.Any(item => string.Equals(item.DiscoveryId, discoveryId, StringComparison.Ordinal)))
        {
            return Task.FromResult(Result.Fail("discovery.not_found", "未找到该发现条目。"));
        }

        _store.Save(catalog with
        {
            GeneratedAt = _time.GetUtcNow(),
            Entries = catalog.Entries
                .Where(item => !string.Equals(item.DiscoveryId, discoveryId, StringComparison.Ordinal))
                .ToArray(),
        });
        return Task.FromResult(Result.Ok());
    }

    public Task<Result<int>> ForgetRootEntriesAsync(string rootId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var fileLock = CrossProcessFileLock.TryAcquire(
            _paths.Root,
            "discovery.lock",
            TimeSpan.FromSeconds(2),
            cancellationToken);
        var load = _store.Load();
        if (load.IsUnsupported)
        {
            return Task.FromResult(Result<int>.Fail(load.ErrorCode!, load.ErrorMessage!));
        }

        var catalog = load.Catalog;
        var remaining = catalog.Entries
            .Where(item => !string.Equals(item.RootId, rootId, StringComparison.Ordinal))
            .ToArray();
        var removed = catalog.Entries.Count - remaining.Length;
        _store.Save(catalog with
        {
            GeneratedAt = _time.GetUtcNow(),
            Roots = catalog.Roots.Where(item => !string.Equals(item.RootId, rootId, StringComparison.Ordinal)).ToArray(),
            Entries = remaining,
        });
        return Task.FromResult(Result<int>.Ok(removed));
    }

    public Task<Result<DiscoveryEntry>> MarkImportedAsync(
        string discoveryId,
        string instanceId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var fileLock = CrossProcessFileLock.TryAcquire(
            _paths.Root,
            "discovery.lock",
            TimeSpan.FromSeconds(2),
            cancellationToken);
        var load = _store.Load();
        if (load.IsUnsupported)
        {
            return Task.FromResult(Result<DiscoveryEntry>.Fail(load.ErrorCode!, load.ErrorMessage!));
        }

        var catalog = load.Catalog;
        var entry = catalog.Entries.FirstOrDefault(item =>
            string.Equals(item.DiscoveryId, discoveryId, StringComparison.Ordinal));
        if (entry is null)
        {
            return Task.FromResult(Result<DiscoveryEntry>.Fail("discovery.not_found", "未找到该发现条目。"));
        }

        var updated = entry with { ImportedInstanceId = instanceId, LastScanId = entry.LastScanId };
        _store.Save(catalog with
        {
            GeneratedAt = _time.GetUtcNow(),
            Entries = catalog.Entries
                .Select(item => string.Equals(item.DiscoveryId, discoveryId, StringComparison.Ordinal) ? updated : item)
                .ToArray(),
        });
        return Task.FromResult(Result<DiscoveryEntry>.Ok(updated));
    }

    public Task<Result<DiscoveryEntry>> ClearImportedAsync(
        string discoveryId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var fileLock = CrossProcessFileLock.TryAcquire(
            _paths.Root,
            "discovery.lock",
            TimeSpan.FromSeconds(2),
            cancellationToken);
        var load = _store.Load();
        if (load.IsUnsupported)
        {
            return Task.FromResult(Result<DiscoveryEntry>.Fail(load.ErrorCode!, load.ErrorMessage!));
        }

        var catalog = load.Catalog;
        var entry = catalog.Entries.FirstOrDefault(item =>
            string.Equals(item.DiscoveryId, discoveryId, StringComparison.Ordinal));
        if (entry is null)
        {
            return Task.FromResult(Result<DiscoveryEntry>.Fail("discovery.not_found", "未找到该发现条目。"));
        }

        var updated = entry with { ImportedInstanceId = null };
        _store.Save(catalog with
        {
            GeneratedAt = _time.GetUtcNow(),
            Entries = catalog.Entries
                .Select(item => string.Equals(item.DiscoveryId, discoveryId, StringComparison.Ordinal) ? updated : item)
                .ToArray(),
        });
        return Task.FromResult(Result<DiscoveryEntry>.Ok(updated));
    }

    private List<WorkspaceRoot> ResolveRoots(DiscoveryScanRequest request)
    {
        var requested = request.RootIds is { Count: > 0 }
            ? new HashSet<string>(request.RootIds, StringComparer.Ordinal)
            : null;
        return _workspace.Roots
            .Where(root => requested is null ? root.Enabled : requested.Contains(root.Id))
            .ToList();
    }

    private async Task<RootScan> ScanRootAsync(
        WorkspaceRoot root,
        DiscoveryScanRequest request,
        string scanId,
        IProgress<DiscoveryProgress>? progress,
        CancellationToken cancellationToken)
    {
        var started = _time.GetUtcNow();
        var policy = (root.ScanPolicy ?? WorkspaceScanPolicy.Default).Clamp();
        var maxEntries = Math.Clamp(
            request.MaxEntriesPerRoot ?? policy.MaxEntries,
            1,
            WorkspaceScanPolicy.MaxEntriesCeiling);
        var maxDepth = Math.Clamp(request.MaxDepth ?? policy.MaxDepth, 1, WorkspaceScanPolicy.MaxDepthCeiling);
        var timeout = request.PerRootTimeout ?? TimeSpan.FromSeconds(policy.PerRootTimeoutSeconds);

        var errors = new List<DiscoveryError>();
        var entries = new List<DiscoveryEntry>();
        var counters = new ScanCounters();
        var structure = InstanceRootStructure.Unknown;
        var rootStatus = root.Status;
        var timedOut = false;
        var cancelled = false;
        var entryList = new List<DiscoveryEntry>();

        if (!Directory.Exists(root.Path))
        {
            var report = new DiscoveryRootReport(
                root.Id,
                WorkspaceStatus.Missing,
                InstanceRootStructure.Unknown,
                0,
                0,
                0,
                0,
                0,
                0,
                1,
                TimedOut: false,
                Cancelled: false,
                _time.GetUtcNow() - started,
                [new DiscoveryError(root.Id, null, "source.missing", "来源目录不存在或当前不可访问。")]);
            return new RootScan(report, entryList, WorkspaceStatus.Missing, null);
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            try
            {
                var scanToken = timeoutSource.Token;
                structure = DetectStructure(root.Path);
                switch (structure)
                {
                    case InstanceRootStructure.EclManaged when request.IncludeManagedForeign:
                        ScanEclManaged(root, scanId, maxEntries, entries, counters, scanToken);
                        break;
                    case InstanceRootStructure.EclManaged:
                        counters.SkippedByExclusion++;
                        errors.Add(new DiscoveryError(
                            root.Id,
                            null,
                            "discovery.adoption_required",
                            "已跳过其他 ECL 工作区的实例记录（需要归属迁移）。"));
                        break;
                    case InstanceRootStructure.Versioned:
                        ScanVersioned(root, scanId, maxEntries, request.ReadVersionMetadata, entries, counters, scanToken);
                        break;
                    case InstanceRootStructure.SingleInstance:
                        ScanSingle(root, scanId, request.ReadVersionMetadata, entries, counters, scanToken);
                        break;
                    default:
                        errors.Add(new DiscoveryError(
                            root.Id,
                            null,
                            "source.unknown_structure",
                            "未识别到受支持的实例结构；未自动导入或清理任何内容。"));
                        break;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                throw;
            }
            catch (OperationCanceledException)
            {
                timedOut = true;
                errors.Add(new DiscoveryError(
                    root.Id,
                    null,
                    "source.scan_timeout",
                    $"扫描超过 {timeout.TotalSeconds:0} 秒，已保留已发现条目。"));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancelled = true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            errors.Add(new DiscoveryError(root.Id, null, "source.access_denied", exception.Message));
            rootStatus = WorkspaceStatus.NotAccessible;
        }

        if (cancelled)
        {
            return new RootScan(
                new DiscoveryRootReport(
                    root.Id,
                    rootStatus,
                    structure,
                    0,
                    counters.DirectoriesScanned,
                    counters.FilesInspected,
                    counters.SkippedByExclusion,
                    counters.SkippedByLimit,
                    counters.SkippedByReparsePoint,
                    errors.Count,
                    TimedOut: false,
                    Cancelled: true,
                    _time.GetUtcNow() - started,
                    errors),
                [],
                rootStatus,
                null);
        }

        var now = _time.GetUtcNow();
        foreach (var entry in entries)
        {
            progress?.Report(new DiscoveryProgress(root.Id, entry.DiscoveryId, entries.Count, "entries"));
            entryList.Add(entry);
        }

        if (counters.SkippedByLimit > 0)
        {
            errors.Add(new DiscoveryError(
                root.Id,
                null,
                "source.limit_reached",
                $"扫描条目超过上限（{maxEntries}），结果不完整。"));
        }

        _ = maxDepth;

        if (rootStatus == WorkspaceStatus.Ready && structure == InstanceRootStructure.Unknown)
        {
            rootStatus = WorkspaceStatus.Ready;
        }

        var finalStatus = rootStatus == WorkspaceStatus.Unknown ? WorkspaceStatus.Ready : rootStatus;
        var finalReport = new DiscoveryRootReport(
            root.Id,
            finalStatus,
            structure,
            entries.Count,
            counters.DirectoriesScanned,
            counters.FilesInspected,
            counters.SkippedByExclusion,
            counters.SkippedByLimit,
            counters.SkippedByReparsePoint,
            errors.Count,
            timedOut,
            Cancelled: false,
            now - started,
            errors);
        return new RootScan(finalReport, entryList, finalStatus, structure);
    }

    private void ScanVersioned(
        WorkspaceRoot root,
        string scanId,
        int maxEntries,
        bool readMetadata,
        List<DiscoveryEntry> entries,
        ScanCounters counters,
        CancellationToken cancellationToken)
    {
        var versionsDirectory = Path.Combine(root.Path, "versions");
        var directories = EnumerateBounded(versionsDirectory, maxEntries, counters);
        var candidates = new List<PendingVersion>();
        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            counters.DirectoriesScanned++;
            if (IsReparsePoint(directory))
            {
                counters.SkippedByReparsePoint++;
                continue;
            }

            var versionId = Path.GetFileName(directory);
            if (versionId.StartsWith(".ecl-", StringComparison.OrdinalIgnoreCase))
            {
                counters.SkippedByExclusion++;
                continue;
            }

            var manifestPath = Path.Combine(directory, versionId + ".json");
            if (!File.Exists(manifestPath))
            {
                counters.SkippedByExclusion++;
                continue;
            }

            string? gameVersion = null;
            var loader = "unknown";
            string? problemCode = null;
            string? problemDetail = null;
            if (readMetadata)
            {
                var manifest = ReadVersionManifest(manifestPath, counters);
                gameVersion = manifest.GameVersion;
                loader = manifest.Loader;
                problemCode = manifest.ProblemCode;
                problemDetail = manifest.ProblemDetail;
            }

            var isolated = IsolationMarkers.Any(marker =>
                File.Exists(Path.Combine(directory, marker)) || Directory.Exists(Path.Combine(directory, marker)));
            candidates.Add(new PendingVersion(directory, versionId, gameVersion, loader, isolated, problemCode, problemDetail));
        }

        var nonIsolated = candidates.Where(candidate => !candidate.Isolated).ToArray();
        foreach (var candidate in candidates)
        {
            var gameDirectory = candidate.Isolated ? candidate.Directory : root.Path;
            var marker = TryReadMarker(
                gameDirectory,
                counters,
                out var markerInstanceId,
                out var markerProblem);
            var kind = !candidate.Isolated && nonIsolated.Length > 1
                ? DiscoveryEntryKind.SharedRootCandidate
                : DiscoveryEntryKind.ExternalVersioned;
            var problemCode = candidate.ProblemCode ?? markerProblem;
            var status = kind == DiscoveryEntryKind.SharedRootCandidate
                ? DiscoveryEntryStatus.Active
                : problemCode is null
                    ? DiscoveryEntryStatus.Active
                    : DiscoveryEntryStatus.Invalid;
            var relative = $"versions/{candidate.VersionId}";
            entries.Add(CreateEntry(
                root,
                scanId,
                relative,
                gameDirectory,
                kind,
                status,
                candidate.VersionId,
                candidate.GameVersion,
                candidate.Loader,
                candidate.VersionId,
                markerInstanceId,
                problemCode,
                candidate.ProblemDetail));
            _ = marker;
        }

        if (nonIsolated.Length > 1)
        {
            // 多个非隔离版本共享游戏根：不创建可启动实例，标记等待用户确认。
            foreach (var entry in entries.Where(item => item.Kind == DiscoveryEntryKind.SharedRootCandidate))
            {
                _ = entry;
            }
        }
    }

    private void ScanEclManaged(
        WorkspaceRoot root,
        string scanId,
        int maxEntries,
        List<DiscoveryEntry> entries,
        ScanCounters counters,
        CancellationToken cancellationToken)
    {
        var instancesDirectory = Path.Combine(root.Path, "instances");
        var directories = EnumerateBounded(instancesDirectory, maxEntries, counters);
        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            counters.DirectoriesScanned++;
            if (IsReparsePoint(directory))
            {
                counters.SkippedByReparsePoint++;
                continue;
            }

            var metadataPath = Path.Combine(directory, "instance.json");
            if (!File.Exists(metadataPath))
            {
                counters.SkippedByExclusion++;
                continue;
            }

            var instanceId = Path.GetFileName(directory);
            if (!AppPaths.IsValidInstanceId(instanceId))
            {
                counters.SkippedByExclusion++;
                continue;
            }

            var metadata = ReadInstanceMetadata(metadataPath, counters);
            if (metadata.ProblemCode is not null)
            {
                entries.Add(CreateEntry(
                    root,
                    scanId,
                    $"instances/{instanceId}",
                    Path.Combine(directory, "minecraft"),
                    DiscoveryEntryKind.ManagedForeign,
                    DiscoveryEntryStatus.Invalid,
                    metadata.Name ?? instanceId,
                    metadata.GameVersion,
                    metadata.Loader ?? "unknown",
                    instanceId,
                    null,
                    metadata.ProblemCode,
                    metadata.ProblemDetail));
                continue;
            }

            var gameDirectory = metadata.GameDirectory is { Length: > 0 } declared
                ? (Path.IsPathRooted(declared)
                    ? declared
                    : Path.Combine(directory, declared))
                : Path.Combine(directory, "minecraft");
            var marker = TryReadMarker(gameDirectory, counters, out var markerInstanceId, out var markerProblem);
            _ = marker;
            entries.Add(CreateEntry(
                root,
                scanId,
                $"instances/{instanceId}",
                gameDirectory,
                DiscoveryEntryKind.ManagedForeign,
                DiscoveryEntryStatus.AdoptionRequired,
                metadata.Name ?? instanceId,
                metadata.GameVersion,
                metadata.Loader ?? "unknown",
                instanceId,
                markerInstanceId,
                markerProblem,
                markerProblem is null ? "记录位于来源根的 instances 目录，需要归属迁移。" : null));

        }
    }

    private void ScanSingle(
        WorkspaceRoot root,
        string scanId,
        bool readMetadata,
        List<DiscoveryEntry> entries,
        ScanCounters counters,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        counters.DirectoriesScanned++;
        var marker = TryReadMarker(root.Path, counters, out var markerInstanceId, out var markerProblem);
        _ = marker;
        string? gameVersion = null;
        string loader = "unknown";
        if (readMetadata)
        {
            var versionsDirectory = Path.Combine(root.Path, "versions");
            if (Directory.Exists(versionsDirectory))
            {
                var version = Directory.EnumerateDirectories(versionsDirectory)
                    .FirstOrDefault(directory => File.Exists(Path.Combine(directory, Path.GetFileName(directory) + ".json")));
                if (version is not null)
                {
                    counters.FilesInspected++;
                    var manifest = ReadVersionManifest(
                        Path.Combine(version, Path.GetFileName(version) + ".json"),
                        counters);
                    gameVersion = manifest.GameVersion;
                    loader = manifest.Loader;
                }
            }
        }

        entries.Add(CreateEntry(
            root,
            scanId,
            string.Empty,
            root.Path,
            DiscoveryEntryKind.ExternalSingle,
            markerProblem is null ? DiscoveryEntryStatus.Active : DiscoveryEntryStatus.Invalid,
            root.Name,
            gameVersion,
            loader,
            null,
            markerInstanceId,
            markerProblem,
            null));
    }

    private Result CommitUnderLock(
        DiscoveryCatalog existing,
        IReadOnlyList<WorkspaceRoot> roots,
        IReadOnlyDictionary<string, List<DiscoveryEntry>> candidates,
        IReadOnlyDictionary<string, DiscoveryRootState> rootStates,
        string scanId,
        List<DiscoveryChange> changes,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var fileLock = CrossProcessFileLock.TryAcquire(
            _paths.Root,
            "discovery.lock",
            TimeSpan.FromSeconds(5),
            cancellationToken);
        if (fileLock is null)
        {
            return Result.Fail(
                "discovery.busy",
                "另一个 ECL 进程正在写入发现目录，请稍后重试。");
        }

        var latestLoad = _store.Load();
        if (latestLoad.IsUnsupported)
        {
            return Result.Fail(latestLoad.ErrorCode!, latestLoad.ErrorMessage!);
        }

        if (latestLoad.ErrorMessage is not null)
        {
            warnings.Add(latestLoad.ErrorMessage);
        }

        var latest = latestLoad.Catalog;
        var knownRootIds = _workspace.Roots.Select(root => root.Id).ToHashSet(StringComparer.Ordinal);
        var merged = latest.Entries.ToList();
        var scannedRootIds = roots.Select(root => root.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var root in roots)
        {
            var rootComplete = rootStates.TryGetValue(root.Id, out var state) && state.IsComplete;
            var fresh = candidates.TryGetValue(root.Id, out var found) ? found : [];
            var oldForRoot = merged.Where(entry => string.Equals(entry.RootId, root.Id, StringComparison.Ordinal)).ToArray();
            var freshIds = fresh.Select(entry => entry.DiscoveryId).ToHashSet(StringComparer.Ordinal);

            if (rootComplete)
            {
                foreach (var old in oldForRoot.Where(entry => !freshIds.Contains(entry.DiscoveryId)))
                {
                    var missing = old with
                    {
                        Status = DiscoveryEntryStatus.Missing,
                        ImportedInstanceId = old.ImportedInstanceId,
                    };
                    merged[merged.IndexOf(old)] = missing;
                    changes.Add(new DiscoveryChange("Missing", root.Id, old.DiscoveryId, "本次扫描未发现该条目。"));
                }
            }

            foreach (var candidate in fresh)
            {
                var previous = merged.FirstOrDefault(entry =>
                    string.Equals(entry.DiscoveryId, candidate.DiscoveryId, StringComparison.Ordinal));
                var mergedEntry = previous is null
                    ? candidate
                    : candidate with
                    {
                        FirstSeenAt = previous.FirstSeenAt == default ? candidate.FirstSeenAt : previous.FirstSeenAt,
                        ImportedInstanceId = previous.ImportedInstanceId,
                        DuplicateResolution = previous.DuplicateResolution,
                        DuplicateGroupId = previous.DuplicateResolution == "keep-separate" ? null : previous.DuplicateGroupId,
                        DuplicateOfDiscoveryId = previous.DuplicateResolution == "keep-separate" ? null : previous.DuplicateOfDiscoveryId,
                        Status = previous.DuplicateResolution == "keep-separate"
                            ? DiscoveryEntryStatus.Active
                            : candidate.Status,
                    };

                if (previous is null)
                {
                    merged.Add(mergedEntry);
                    changes.Add(new DiscoveryChange("Added", root.Id, candidate.DiscoveryId, candidate.DisplayName));
                }
                else
                {
                    var index = merged.IndexOf(previous);
                    merged[index] = mergedEntry;
                    changes.Add(new DiscoveryChange("Updated", root.Id, candidate.DiscoveryId, candidate.DisplayName));
                }
            }
        }

        // 已解绑来源的条目转为 Unbound；未参与本次扫描的根条目保留原状态。
        for (var i = 0; i < merged.Count; i++)
        {
            if (!knownRootIds.Contains(merged[i].RootId) &&
                merged[i].Status != DiscoveryEntryStatus.Unbound)
            {
                merged[i] = merged[i] with { Status = DiscoveryEntryStatus.Unbound };
                changes.Add(new DiscoveryChange("Unbound", merged[i].RootId, merged[i].DiscoveryId, "来源已解除登记。"));
            }
        }

        // 来源不可访问：条目保留，状态 SourceMissing。
        foreach (var root in roots)
        {
            var unavailable = !Directory.Exists(root.Path);
            if (!unavailable)
            {
                continue;
            }

            for (var i = 0; i < merged.Count; i++)
            {
                if (string.Equals(merged[i].RootId, root.Id, StringComparison.Ordinal))
                {
                    merged[i] = merged[i] with { Status = DiscoveryEntryStatus.SourceMissing };
                }
            }
        }

        ApplyDuplicates(merged);

        var catalog = latest with
        {
            SchemaVersion = DiscoveryCatalog.CurrentSchemaVersion,
            GeneratedAt = _time.GetUtcNow(),
            Roots = latest.Roots
                .Where(state => knownRootIds.Contains(state.RootId))
                .Concat(rootStates.Values)
                .GroupBy(state => state.RootId, StringComparer.Ordinal)
                .Select(group => group.Last())
                .ToArray(),
            Entries = merged,
        };
        _store.Save(catalog);
        _ = scanId;
        _ = scannedRootIds;
        return Result.Ok();
    }

    private async Task ApplyRootStateAsync(
        WorkspaceRoot root,
        DiscoveryRootReport report,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = _time.GetUtcNow();
        var errorCode = report.Errors.FirstOrDefault()?.Code;
        var errorMessage = report.Errors.FirstOrDefault()?.Detail;
        var status = report.RootStatus;
        if (report.TimedOut)
        {
            status = root.Status;
            errorCode = "source.scan_timeout";
            errorMessage = "扫描超时，结果不完整。";
        }

        var updated = root with
        {
            Status = status,
            Structure = report.Structure.ToString(),
            LastScanAttemptAtUtc = now,
            LastScannedAtUtc = report.IsComplete ? now : root.LastScannedAtUtc,
            LastScanError = errorMessage,
            LastScanErrorCode = errorCode,
            CanonicalPath = PathIdentity.TryGetFinalPath(root.Path) ?? root.CanonicalPath,
            PathIdentityKey = PathIdentity.TryGetKey(root.Path) ?? root.PathIdentityKey,
        };
        await _workspace.UpdateRootAsync(updated, cancellationToken).ConfigureAwait(false);
    }

    private async Task UpdateRootAttemptsAsync(
        IReadOnlyList<WorkspaceRoot> roots,
        string code,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var root in roots)
        {
            var updated = root with
            {
                LastScanAttemptAtUtc = _time.GetUtcNow(),
                LastScanErrorCode = code,
            };
            await _workspace.UpdateRootAsync(updated, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static DiscoveryCatalog ReconcileUnbound(DiscoveryCatalog catalog)
    {
        // 读取时无法知道全部已登记根；只标注明显缺少根登记的条目由扫描提交完成。
        return catalog;
    }

    private static bool IsActiveEntry(DiscoveryEntry entry) =>
        entry.Status is not (DiscoveryEntryStatus.Missing or DiscoveryEntryStatus.Unbound or DiscoveryEntryStatus.SourceMissing);

    private static bool SamePhysicalPath(DiscoveryEntry left, DiscoveryEntry right)
    {
        if (!string.IsNullOrWhiteSpace(left.PathIdentityKey) &&
            string.Equals(left.PathIdentityKey, right.PathIdentityKey, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(
            PathIdentity.Normalize(left.CanonicalGameDirectory ?? left.GameDirectory),
            PathIdentity.Normalize(right.CanonicalGameDirectory ?? right.GameDirectory),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 跨根去重与冲突（F-02 §8.5）：路径身份优先，其次最终路径；标记相同但路径不同 → IdentityConflict。
    /// 不按名称/版本/时间自动决定代表；用户显式 keep-separate 的条目保留独立状态。
    /// </summary>
    private static void ApplyDuplicates(List<DiscoveryEntry> entries)
    {
        var candidates = entries
            .Where(entry => entry.Kind != DiscoveryEntryKind.SharedRootCandidate)
            .Where(entry => !string.Equals(entry.DuplicateResolution, "keep-separate", StringComparison.Ordinal))
            .ToArray();

        var groups = new Dictionary<string, List<DiscoveryEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in candidates)
        {
            var key = !string.IsNullOrWhiteSpace(entry.PathIdentityKey)
                ? "id:" + entry.PathIdentityKey
                : "path:" + PathIdentity.Normalize(entry.CanonicalGameDirectory ?? entry.GameDirectory);
            if (!groups.TryGetValue(key, out var list))
            {
                list = [];
                groups[key] = list;
            }

            list.Add(entry);
        }

        foreach (var group in groups)
        {
            if (group.Value.Count < 2)
            {
                continue;
            }

            var representative = group.Value
                .OrderByDescending(entry => entry.ImportedInstanceId is not null)
                .ThenBy(entry => entry.RootId, StringComparer.Ordinal)
                .ThenBy(entry => entry.RelativePath, StringComparer.Ordinal)
                .First();
            var groupId = "dup-" + Convert.ToHexStringLower(
                SHA256.HashData(Encoding.UTF8.GetBytes(group.Key)))[..16];
            foreach (var entry in group.Value)
            {
                var index = entries.IndexOf(entry);
                if (index < 0)
                {
                    continue;
                }

                entries[index] = string.Equals(entry.DiscoveryId, representative.DiscoveryId, StringComparison.Ordinal)
                    ? entry with
                    {
                        DuplicateGroupId = groupId,
                        DuplicateOfDiscoveryId = null,
                        Status = entry.Status is DiscoveryEntryStatus.Invalid
                            ? DiscoveryEntryStatus.Invalid
                            : DiscoveryEntryStatus.Active,
                    }
                    : entry with
                    {
                        DuplicateGroupId = groupId,
                        DuplicateOfDiscoveryId = representative.DiscoveryId,
                        Status = DiscoveryEntryStatus.PotentialDuplicate,
                    };
            }
        }

        // 同一标记 InstanceId 指向不同物理目录 → IdentityConflict（不合并、不删除）。
        var markerGroups = entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.MarkerInstanceId))
            .GroupBy(entry => entry.MarkerInstanceId!, StringComparer.Ordinal)
            .Where(group => group
                .Select(entry => PathIdentity.Normalize(entry.CanonicalGameDirectory ?? entry.GameDirectory))
                .Where(path => path is not null)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() > 1);
        foreach (var group in markerGroups)
        {
            var first = group.First();
            foreach (var entry in group)
            {
                var index = entries.IndexOf(entry);
                if (index < 0)
                {
                    continue;
                }

                entries[index] = entry with
                {
                    Status = DiscoveryEntryStatus.IdentityConflict,
                    ProblemCode = "discovery.identity_conflict",
                    ProblemDetail = $"多个目录声明同一实例身份：{group.Key}",
                    DuplicateOfDiscoveryId = string.Equals(entry.DiscoveryId, first.DiscoveryId, StringComparison.Ordinal)
                        ? null
                        : first.DiscoveryId,
                };
            }
        }
    }

    private static DiscoveryEntry CreateEntry(
        WorkspaceRoot root,
        string scanId,
        string relativePath,
        string gameDirectory,
        DiscoveryEntryKind kind,
        DiscoveryEntryStatus status,
        string displayName,
        string? gameVersion,
        string loader,
        string? versionId,
        string? markerInstanceId,
        string? problemCode,
        string? problemDetail)
    {
        var normalizedGameDirectory = PathIdentity.Normalize(gameDirectory) ?? gameDirectory;
        var canonical = PathIdentity.TryGetFinalPath(normalizedGameDirectory) ?? normalizedGameDirectory;
        var identity = PathIdentity.TryGetKey(normalizedGameDirectory);
        var relative = relativePath.Replace('\\', '/');
        var now = DateTimeOffset.UtcNow;
        return new DiscoveryEntry
        {
            DiscoveryId = CreateDiscoveryId(root.Id, relative),
            RootId = root.Id,
            RelativePath = relative,
            GameDirectory = normalizedGameDirectory,
            CanonicalGameDirectory = canonical,
            PathIdentityKey = identity,
            Kind = kind,
            Status = status,
            DisplayName = displayName,
            GameVersion = gameVersion,
            Loader = string.IsNullOrWhiteSpace(loader) ? "unknown" : loader,
            VersionId = versionId,
            MarkerInstanceId = markerInstanceId,
            ProblemCode = problemCode,
            ProblemDetail = problemDetail,
            FirstSeenAt = now,
            LastSeenAt = now,
            LastScanId = scanId,
        };
    }

    internal static string CreateDiscoveryId(string rootId, string relativePath)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{rootId}|{relativePath.Replace('\\', '/').ToLowerInvariant()}"));
        return "disc-" + Convert.ToHexStringLower(hash)[..16];
    }

    private static List<string> EnumerateBounded(string directory, int limit, ScanCounters counters)
    {
        var result = new List<string>();
        try
        {
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                if (result.Count >= limit)
                {
                    counters.SkippedByLimit++;
                    break;
                }

                result.Add(child);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            counters.SkippedByExclusion++;
        }

        return result;
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool TryReadMarker(
        string gameDirectory,
        ScanCounters counters,
        out string? instanceId,
        out string? problemCode)
    {
        instanceId = null;
        problemCode = null;
        var path = Path.Combine(gameDirectory, ".ecl", "instance.json");
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            counters.FilesInspected++;
            var info = new FileInfo(path);
            if (info.Length > MaxMarkerBytes)
            {
                problemCode = "discovery.invalid_metadata";
                return false;
            }

            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(
                stream,
                new JsonDocumentOptions { MaxDepth = 8, AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("instanceId", out var idValue) ||
                idValue.ValueKind != JsonValueKind.String)
            {
                problemCode = "discovery.invalid_metadata";
                return false;
            }

            instanceId = idValue.GetString();
            return !string.IsNullOrWhiteSpace(instanceId);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            problemCode = "discovery.invalid_metadata";
            return false;
        }
    }

    private static (string? GameVersion, string Loader, string? ProblemCode, string? ProblemDetail) ReadVersionManifest(
        string path,
        ScanCounters counters)
    {
        counters.FilesInspected++;
        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxManifestBytes)
            {
                return (null, "unknown", "discovery.invalid_manifest", "版本清单超过大小上限。");
            }

            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(
                stream,
                new JsonDocumentOptions { MaxDepth = MaxJsonDepth, AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = document.RootElement;
            var id = ReadString(root, "id");
            var clientVersion = ReadString(root, "clientVersion");
            var inheritsFrom = ReadString(root, "inheritsFrom");
            var loader = DetectLoader(id);
            var gameVersion = clientVersion ?? (loader == "vanilla" ? id : inheritsFrom) ?? inheritsFrom;
            var problemCode = string.IsNullOrWhiteSpace(gameVersion) ? "discovery.invalid_manifest" : null;
            var detail = problemCode is null ? null : "无法从版本清单确定 Minecraft 版本。";
            return (gameVersion, loader, problemCode, detail);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return (null, "unknown", "discovery.invalid_manifest", $"版本清单读取失败：{exception.Message}");
        }
    }

    private static (string? Name, string? GameVersion, string? Loader, string? GameDirectory, string? ProblemCode, string? ProblemDetail)
        ReadInstanceMetadata(string path, ScanCounters counters)
    {
        counters.FilesInspected++;
        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxInstanceMetadataBytes)
            {
                return (null, null, null, null, "discovery.invalid_metadata", "实例元数据超过大小上限。");
            }

            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(
                stream,
                new JsonDocumentOptions { MaxDepth = MaxJsonDepth, AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = document.RootElement;
            var id = ReadString(root, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                return (null, null, null, null, "discovery.invalid_metadata", "实例元数据缺少 id。");
            }

            return (
                ReadString(root, "name"),
                ReadString(root, "gameVersion"),
                ReadString(root, "loader"),
                ReadString(root, "gameDirectory"),
                null,
                null);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return (null, null, null, null, "discovery.invalid_metadata", $"实例元数据读取失败：{exception.Message}");
        }
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string DetectLoader(string? versionId)
    {
        if (string.IsNullOrWhiteSpace(versionId))
        {
            return "unknown";
        }

        if (versionId.StartsWith("fabric-loader-", StringComparison.OrdinalIgnoreCase))
        {
            return "fabric";
        }

        if (versionId.StartsWith("neoforge-", StringComparison.OrdinalIgnoreCase))
        {
            return "neoforge";
        }

        if (versionId.Contains("cleanroom", StringComparison.OrdinalIgnoreCase))
        {
            return "cleanroom";
        }

        if (versionId.StartsWith("forge-", StringComparison.OrdinalIgnoreCase) ||
            versionId.Contains("forge", StringComparison.OrdinalIgnoreCase))
        {
            return "forge";
        }

        return "vanilla";
    }

    private static InstanceRootStructure DetectStructure(string path)
    {
        var instances = Path.Combine(path, "instances");
        if (Directory.Exists(instances) && HasChild(instances))
        {
            return InstanceRootStructure.EclManaged;
        }

        var versions = Path.Combine(path, "versions");
        if (Directory.Exists(versions) && HasChild(versions))
        {
            return InstanceRootStructure.Versioned;
        }

        if (IsolationMarkers.Any(marker =>
                File.Exists(Path.Combine(path, marker)) || Directory.Exists(Path.Combine(path, marker))))
        {
            return InstanceRootStructure.SingleInstance;
        }

        return InstanceRootStructure.Unknown;
    }

    private static bool HasChild(string directory)
    {
        try
        {
            using var entries = Directory.EnumerateFileSystemEntries(directory).GetEnumerator();
            return entries.MoveNext();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private sealed record PendingVersion(
        string Directory,
        string VersionId,
        string? GameVersion,
        string Loader,
        bool Isolated,
        string? ProblemCode,
        string? ProblemDetail);

    private sealed record RootScan(
        DiscoveryRootReport Report,
        IReadOnlyList<DiscoveryEntry> Entries,
        WorkspaceStatus RootStatus,
        InstanceRootStructure? Structure);

    private sealed class ScanCounters
    {
        public int DirectoriesScanned { get; set; }

        public int FilesInspected { get; set; }

        public int SkippedByExclusion { get; set; }

        public int SkippedByLimit { get; set; }

        public int SkippedByReparsePoint { get; set; }
    }
}
