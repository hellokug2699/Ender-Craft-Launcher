// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.Extensions.Logging;

namespace Ecl.Core.Storage;

/// <summary>一次来源登记的结果：根、路径校验（含警告）与是否新建。</summary>
public sealed record WorkspaceRegistrationResult(WorkspaceRoot Root, WorkspacePathCheck Check, bool Created);

public interface IWorkspaceService
{
    /// <summary>当前持久化状态（只读快照）。</summary>
    WorkspaceFile State { get; }

    WorkspaceRoot? Primary { get; }

    IReadOnlyList<WorkspaceRoot> Roots { get; }

    /// <summary>用户已确认工作区且存在主工作区。仅表示“已配置”，可用性需另行校验。</summary>
    bool IsConfigured { get; }

    event EventHandler? Changed;

    IReadOnlyList<WorkspaceSuggestion> Suggest();

    WorkspacePathCheck Check(string path, bool createIfMissing);

    WorkspacePathCheck Check(string path, WorkspaceCheckMode mode);

    Task<Result<WorkspaceRoot>> InitializeAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// 登记外部来源根目录。只读校验，不写来源目录；同一来源幂等返回；
    /// 已由其他根占用的同一物理目录返回 <c>source.already_registered</c>。
    /// 登记本身不扫描、不导入任何实例。
    /// </summary>
    Task<Result<WorkspaceRoot>> RegisterExternalAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>登记外部来源并保留路径校验警告（UI 决定是否提示/阻止）。</summary>
    Task<Result<WorkspaceRegistrationResult>> RegisterExternalDetailedAsync(
        string path,
        CancellationToken cancellationToken = default);

    /// <summary>登记一个已存在的受管理根目录；不创建新布局、不迁移已有实例。</summary>
    Task<Result<WorkspaceRoot>> AddManagedRootAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>重新定位已登记根的路径；保留根 ID，不移动文件，不改变已导入实例的 GameDirectory。</summary>
    Task<Result<WorkspaceRoot>> RelocateRootAsync(
        string rootId,
        string newPath,
        CancellationToken cancellationToken = default);

    /// <summary>只读校验某个已登记根并更新状态；不扫描、不写来源目录。</summary>
    Task<Result<WorkspacePathCheck>> ValidateRootAsync(string rootId, CancellationToken cancellationToken = default);

    Task<Result<WorkspacePathCheck>> ValidatePrimaryAsync(CancellationToken cancellationToken = default);

    /// <summary>用户选择“稍后配置”。允许浏览，但在首次创建/下载/启动前仍会要求确认。</summary>
    Task SkipOnboardingAsync(CancellationToken cancellationToken = default);

    /// <summary>更新已登记来源的扫描状态/显示名称；只修改登记，不触碰来源文件。</summary>
    Task UpdateRootAsync(WorkspaceRoot root, CancellationToken cancellationToken = default);

    /// <summary>移除来源登记（默认只解除登记），不删除来源目录中的任何文件。</summary>
    Task<Result<bool>> RemoveRootAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// 按 <see cref="RootRemovalMode"/> 移除来源登记。解绑前必须通过活动探测；
    /// 探测不可用时保守阻止（<c>source.activity_unknown</c>）。
    /// </summary>
    Task<Result<bool>> RemoveRootAsync(
        string id,
        RootRemovalMode mode,
        CancellationToken cancellationToken = default);

    /// <summary>检测旧版布局中仍位于启动器数据目录下的实例/版本数据。</summary>
    WorkspaceLegacyInfo DetectLegacy();

    /// <summary>用户明确选择继续使用旧版数据目录（不推荐，只避免数据丢失）。</summary>
    Task<Result<WorkspaceRoot>> AdoptLegacyAsync(CancellationToken cancellationToken = default);

    StorageLayout GetStorageLayout();
}

/// <summary>
/// 工作区服务：负责选择、登记、校验与状态持久化。路径规则集中在
/// <see cref="WorkspacePathValidator"/>，UI 与 CLI 必须调用同一服务。
/// 所有写入 <c>workspace.json</c> 的操作都持有跨进程锁并有界等待。
/// </summary>
public sealed class WorkspaceService : IWorkspaceService
{
    private static readonly TimeSpan WorkspaceLockTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ActivityProbeTimeout = TimeSpan.FromSeconds(5);

    private readonly AppPaths _paths;
    private readonly WorkspaceStore _store;
    private readonly WorkspacePathValidator _validator;
    private readonly ILogger<WorkspaceService> _logger;
    private readonly IInstanceSourceActivityProbe? _activityProbe;
    private readonly IServiceProvider? _services;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WorkspaceFile _state;

    public WorkspaceService(AppPaths paths, ILogger<WorkspaceService> logger)
        : this(paths, logger, activityProbe: null)
    {
    }

    public WorkspaceService(
        AppPaths paths,
        ILogger<WorkspaceService> logger,
        IInstanceSourceActivityProbe? activityProbe)
    {
        _paths = paths;
        _logger = logger;
        _activityProbe = activityProbe;
        _store = new WorkspaceStore(paths.ConfigDirectory);
        _validator = new WorkspacePathValidator(paths.Root);
        _state = _store.Load(out var error);
        if (error is not null)
        {
            _logger.LogWarning("工作区配置不可用：{Error}", error);
        }
    }

    /// <summary>
    /// 组合根使用：活动探测在 Minecraft 层注册，且其依赖的实例服务会反向读取工作区，
    /// 因此只在实际解绑时惰性解析，避免构造期循环依赖。
    /// </summary>
    public WorkspaceService(AppPaths paths, ILogger<WorkspaceService> logger, IServiceProvider services)
        : this(paths, logger, activityProbe: null)
    {
        _services = services;
    }

    private IInstanceSourceActivityProbe? ResolveActivityProbe() =>
        _activityProbe ?? _services?.GetService(typeof(IInstanceSourceActivityProbe)) as IInstanceSourceActivityProbe;

    public event EventHandler? Changed;

    public WorkspaceFile State => _state;

    public WorkspaceRoot? Primary => _state.PrimaryId is null
        ? null
        : _state.Roots.FirstOrDefault(root => string.Equals(root.Id, _state.PrimaryId, StringComparison.Ordinal));

    public IReadOnlyList<WorkspaceRoot> Roots => _state.Roots;

    public bool IsConfigured => Primary is not null && _state.OnboardingCompletedAtUtc is not null;

    public IReadOnlyList<WorkspaceSuggestion> Suggest() => WorkspaceRecommender.Suggest(_paths);

    public WorkspacePathCheck Check(string path, bool createIfMissing) =>
        _validator.Check(
            path,
            createIfMissing ? WorkspaceCheckMode.CreateIfMissing : WorkspaceCheckMode.RequireExisting);

    public WorkspacePathCheck Check(string path, WorkspaceCheckMode mode) => _validator.Check(path, mode);

    public async Task<Result<WorkspaceRoot>> InitializeAsync(string path, CancellationToken cancellationToken = default)
    {
        var check = Check(path, createIfMissing: true);
        if (!check.IsUsable)
        {
            return Result<WorkspaceRoot>.Fail(
                check.ProblemCode ?? "workspace.invalid",
                check.ProblemMessage ?? "工作区路径不可用。");
        }

        return await Task.Run(
            () =>
            {
                EnsureStructure(check.Path);
                var existing = FindRootByPath(check.Path);

                var root = new WorkspaceRoot(
                    existing?.Id ?? CreateId(check.Path),
                    string.IsNullOrWhiteSpace(existing?.Name) ? CreateName(check.Path) : existing!.Name,
                    check.Path,
                    WorkspaceKind.Managed,
                    WorkspaceStatus.Ready,
                    DateTimeOffset.UtcNow,
                    CanonicalPath: check.CanonicalPath ?? check.Path,
                    PathIdentityKey: check.PathIdentityKey);

                var saved = Save(state => state with
                {
                    PrimaryId = root.Id,
                    Roots = Replace(state.Roots, root),
                    OnboardingCompletedAtUtc = DateTimeOffset.UtcNow,
                    OnboardingSkipped = false,
                });
                if (!saved.IsSuccess)
                {
                    return Result<WorkspaceRoot>.Fail(saved.Error!);
                }

                _paths.SetGameRoot(root.Path);
                _paths.SyncInstanceRootMappings(_state.Roots, _state.PrimaryId);
                _logger.LogInformation("已设置 Minecraft 工作区：{Path}", root.Path);
                return Result<WorkspaceRoot>.Ok(root);
            },
            cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<Result<WorkspaceRoot>> RegisterExternalAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var detailed = await RegisterExternalDetailedAsync(path, cancellationToken).ConfigureAwait(false);
        return detailed.IsSuccess
            ? Result<WorkspaceRoot>.Ok(detailed.Value!.Root)
            : Result<WorkspaceRoot>.Fail(detailed.Error!);
    }

    public async Task<Result<WorkspaceRegistrationResult>> RegisterExternalDetailedAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        // 只读校验：外部来源目录零写入（不写探针/临时文件/锁）。
        var check = Check(path, WorkspaceCheckMode.ReadOnlyExisting);
        if (!check.IsUsable)
        {
            return Result<WorkspaceRegistrationResult>.Fail(
                check.ProblemCode ?? "workspace.invalid",
                check.ProblemMessage ?? "外部实例目录不可用。");
        }

        return await Task.Run(
            () =>
            {
                if (FindRootByPath(check.Path) is { } samePath)
                {
                    if (samePath.Kind != WorkspaceKind.External)
                    {
                        return Result<WorkspaceRegistrationResult>.Fail(
                            "source.already_registered",
                            "该目录已登记为受管理工作区，不能同时作为外部来源。",
                            samePath.Id);
                    }

                    var refreshed = samePath with
                    {
                        Status = WorkspaceStatus.Ready,
                        LastValidatedAtUtc = DateTimeOffset.UtcNow,
                        CanonicalPath = check.CanonicalPath ?? check.Path,
                        PathIdentityKey = check.PathIdentityKey,
                    };
                    var saved = Save(state => state with { Roots = Replace(state.Roots, refreshed) });
                    return saved.IsSuccess
                        ? Result<WorkspaceRegistrationResult>.Ok(
                            new WorkspaceRegistrationResult(refreshed, check, Created: false))
                        : Result<WorkspaceRegistrationResult>.Fail(saved.Error!);
                }

                if (FindConflictingRoot(check) is { } conflict)
                {
                    return Result<WorkspaceRegistrationResult>.Fail(
                        "source.already_registered",
                        "该目录已由另一个登记来源指向（可能是 junction/符号链接别名）。",
                        conflict.Id);
                }

                var root = new WorkspaceRoot(
                    CreateUniqueId(check.Path),
                    CreateName(check.Path),
                    check.Path,
                    WorkspaceKind.External,
                    WorkspaceStatus.Ready,
                    DateTimeOffset.UtcNow,
                    CanonicalPath: check.CanonicalPath ?? check.Path,
                    PathIdentityKey: check.PathIdentityKey);

                var savedExternal = Save(state => state with { Roots = Replace(state.Roots, root) });
                if (!savedExternal.IsSuccess)
                {
                    return Result<WorkspaceRegistrationResult>.Fail(savedExternal.Error!);
                }

                _logger.LogInformation(
                    "已登记外部实例来源（只读，零写入）：{Path}（{RootId}）",
                    root.Path,
                    root.Id);
                return Result<WorkspaceRegistrationResult>.Ok(
                    new WorkspaceRegistrationResult(root, check, Created: true));
            },
            cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<Result<WorkspaceRoot>> AddManagedRootAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var check = Check(path, WorkspaceCheckMode.RequireExisting);
        if (!check.IsUsable)
        {
            return Result<WorkspaceRoot>.Fail(
                check.ProblemCode ?? "workspace.invalid",
                check.ProblemMessage ?? "受管理目录不可用。");
        }

        return await Task.Run(
            () =>
            {
                if (FindRootByPath(check.Path) is { } samePath)
                {
                    if (samePath.Kind != WorkspaceKind.Managed)
                    {
                        return Result<WorkspaceRoot>.Fail(
                            "source.already_registered",
                            "该目录已登记为外部来源，不能同时作为受管理根。",
                            samePath.Id);
                    }

                    return Result<WorkspaceRoot>.Ok(samePath);
                }

                if (FindConflictingRoot(check) is { } conflict)
                {
                    return Result<WorkspaceRoot>.Fail(
                        "source.already_registered",
                        "该目录已由另一个登记来源指向。",
                        conflict.Id);
                }

                var root = new WorkspaceRoot(
                    CreateUniqueId(check.Path),
                    CreateName(check.Path),
                    check.Path,
                    WorkspaceKind.Managed,
                    WorkspaceStatus.Ready,
                    DateTimeOffset.UtcNow,
                    CanonicalPath: check.CanonicalPath ?? check.Path,
                    PathIdentityKey: check.PathIdentityKey);

                var saved = Save(state => state with { Roots = Replace(state.Roots, root) });
                if (!saved.IsSuccess)
                {
                    return Result<WorkspaceRoot>.Fail(saved.Error!);
                }

                _logger.LogInformation("已登记受管理根目录：{Path}（{RootId}）", root.Path, root.Id);
                return Result<WorkspaceRoot>.Ok(root);
            },
            cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<Result<WorkspaceRoot>> RelocateRootAsync(
        string rootId,
        string newPath,
        CancellationToken cancellationToken = default)
    {
        var root = _state.Roots.FirstOrDefault(existing => string.Equals(existing.Id, rootId, StringComparison.Ordinal));
        if (root is null)
        {
            return Result<WorkspaceRoot>.Fail("workspace.root_missing", "未找到该实例来源。");
        }

        if (string.Equals(_state.PrimaryId, rootId, StringComparison.Ordinal))
        {
            return Result<WorkspaceRoot>.Fail(
                "workspace.primary_relocate",
                "主工作区搬迁请使用工作区迁移流程，不能直接重新定位。");
        }

        var check = root.Kind == WorkspaceKind.External
            ? Check(newPath, WorkspaceCheckMode.ReadOnlyExisting)
            : Check(newPath, WorkspaceCheckMode.RequireExisting);
        if (!check.IsUsable)
        {
            return Result<WorkspaceRoot>.Fail(
                check.ProblemCode ?? "source.missing",
                check.ProblemMessage ?? "新路径不可用。");
        }

        return await Task.Run(
            () =>
            {
                var conflict = _state.Roots.FirstOrDefault(other =>
                    !string.Equals(other.Id, rootId, StringComparison.Ordinal) &&
                    MatchesIdentity(other, check));
                if (conflict is not null)
                {
                    return Result<WorkspaceRoot>.Fail(
                        "source.path_conflict",
                        "新路径已属于另一个登记来源。",
                        conflict.Id);
                }

                // 根 ID 保留：重新定位不重算 ID（F-02 §3.6）。
                var updated = root with
                {
                    Path = check.Path,
                    CanonicalPath = check.CanonicalPath ?? check.Path,
                    PathIdentityKey = check.PathIdentityKey,
                    Status = WorkspaceStatus.Ready,
                    Structure = null,
                    LastValidatedAtUtc = DateTimeOffset.UtcNow,
                    LastScanError = null,
                    LastScanErrorCode = null,
                };
                var saved = Save(state => state with { Roots = Replace(state.Roots, updated) });
                if (!saved.IsSuccess)
                {
                    return Result<WorkspaceRoot>.Fail(saved.Error!);
                }

                _logger.LogInformation(
                    "已重新定位实例来源 {RootId}：{OldPath} → {NewPath}（未移动文件）",
                    rootId,
                    root.Path,
                    updated.Path);
                return Result<WorkspaceRoot>.Ok(updated);
            },
            cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<Result<WorkspacePathCheck>> ValidateRootAsync(
        string rootId,
        CancellationToken cancellationToken = default)
    {
        var root = _state.Roots.FirstOrDefault(existing => string.Equals(existing.Id, rootId, StringComparison.Ordinal));
        if (root is null)
        {
            return Result<WorkspacePathCheck>.Fail("workspace.root_missing", "未找到该实例来源。");
        }

        return await Task.Run(
            () =>
            {
                var check = Check(
                    root.Path,
                    root.Kind == WorkspaceKind.External
                        ? WorkspaceCheckMode.ReadOnlyExisting
                        : WorkspaceCheckMode.RequireExisting);
                var updated = root with
                {
                    Status = MapStatus(check),
                    LastValidatedAtUtc = DateTimeOffset.UtcNow,
                    CanonicalPath = check.CanonicalPath ?? root.CanonicalPath,
                    PathIdentityKey = check.PathIdentityKey ?? root.PathIdentityKey,
                };
                Save(state => state with { Roots = Replace(state.Roots, updated) });
                return Result<WorkspacePathCheck>.Ok(check);
            },
            cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<Result<WorkspacePathCheck>> ValidatePrimaryAsync(CancellationToken cancellationToken = default)
    {
        var primary = Primary;
        if (primary is null)
        {
            return Result<WorkspacePathCheck>.Fail("workspace.not_configured", "尚未设置 Minecraft 工作区。");
        }

        return await Task.Run(
            () =>
            {
                var check = Check(primary.Path, createIfMissing: false);
                var updated = primary with
                {
                    Status = MapStatus(check),
                    LastValidatedAtUtc = DateTimeOffset.UtcNow,
                    CanonicalPath = check.CanonicalPath ?? primary.CanonicalPath,
                    PathIdentityKey = check.PathIdentityKey ?? primary.PathIdentityKey,
                };

                Save(state => state with { Roots = Replace(state.Roots, updated) });
                if (!check.IsUsable)
                {
                    _logger.LogWarning(
                        "工作区不可用：{Path}（{Status}，{Problem}）",
                        primary.Path,
                        check.Status,
                        check.ProblemMessage);
                }

                return Result<WorkspacePathCheck>.Ok(check);
            },
            cancellationToken)
            .ConfigureAwait(false);
    }

    public Task UpdateRootAsync(WorkspaceRoot root, CancellationToken cancellationToken = default) =>
        Task.Run(
            () =>
            {
                if (!_state.Roots.Any(existing => string.Equals(existing.Id, root.Id, StringComparison.Ordinal)))
                {
                    return;
                }

                Save(state => state with { Roots = Replace(state.Roots, root) });
            },
            cancellationToken);

    public Task<Result<bool>> RemoveRootAsync(string id, CancellationToken cancellationToken = default) =>
        RemoveRootAsync(id, RootRemovalMode.Unbind, cancellationToken);

    public async Task<Result<bool>> RemoveRootAsync(
        string id,
        RootRemovalMode mode,
        CancellationToken cancellationToken = default)
    {
        var root = _state.Roots.FirstOrDefault(existing => string.Equals(existing.Id, id, StringComparison.Ordinal));
        if (root is null)
        {
            return Result<bool>.Fail("workspace.root_missing", "未找到该实例来源。");
        }

        if (string.Equals(_state.PrimaryId, id, StringComparison.Ordinal))
        {
            return Result<bool>.Fail("workspace.primary_remove", "主工作区不能直接移除，请先切换到其它工作区。");
        }

        var activityProbe = ResolveActivityProbe();
        if (activityProbe is null)
        {
            // 无法证明无活动会话：保守阻止，避免产生悬空会话/写入引用。
            return Result<bool>.Fail(
                "source.activity_unknown",
                "无法确认该来源下是否有正在运行的游戏或写入任务，已阻止解绑。");
        }

        Result<IReadOnlyList<ActiveInstanceReference>> activity;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ActivityProbeTimeout);
            activity = await activityProbe.FindActiveAsync(id, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Result<bool>.Fail("source.activity_unknown", "探测活动会话超时，已阻止解绑。");
        }

        if (!activity.IsSuccess)
        {
            return Result<bool>.Fail(
                "source.activity_unknown",
                "无法确认该来源下是否有活动会话，已阻止解绑。",
                activity.Error?.Message);
        }

        if (activity.Value is { Count: > 0 } active)
        {
            var detail = string.Join(
                "; ",
                active.Select(item =>
                    string.IsNullOrWhiteSpace(item.SessionId)
                        ? item.InstanceId
                        : $"{item.InstanceId}({item.SessionId})"));
            return Result<bool>.Fail(
                "source.busy",
                "该来源下仍有活动会话或写入任务，请先关闭游戏/等待任务完成。",
                detail);
        }

        return await Task.Run(
            () =>
            {
                // 只解除登记；不删除来源目录、实例或用户数据。
                var saved = Save(state => state with
                {
                    Roots = state.Roots
                        .Where(existing => !string.Equals(existing.Id, id, StringComparison.Ordinal))
                        .ToArray(),
                });
                if (!saved.IsSuccess)
                {
                    return Result<bool>.Fail(saved.Error!);
                }

                _logger.LogInformation(
                    "已解除实例来源登记（{Mode}，未删除文件）：{Path}",
                    mode,
                    root.Path);
                return Result<bool>.Ok(true);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public Task SkipOnboardingAsync(CancellationToken cancellationToken = default) =>
        Task.Run(
            () => Save(state => state with { OnboardingSkipped = true }),
            cancellationToken);

    public WorkspaceLegacyInfo DetectLegacy()
    {
        if (Primary is not null)
        {
            return new WorkspaceLegacyInfo(false, _paths.Root, 0, 0);
        }

        var instances = CountDirectories(Path.Combine(_paths.Root, "instances"));
        var versions = CountDirectories(Path.Combine(_paths.Root, "versions"));
        return new WorkspaceLegacyInfo(instances > 0 || versions > 0, _paths.Root, instances, versions);
    }

    public Task<Result<WorkspaceRoot>> AdoptLegacyAsync(CancellationToken cancellationToken = default) =>
        Task.Run(
            () =>
            {
                var legacy = DetectLegacy();
                if (!legacy.Found)
                {
                    return Result<WorkspaceRoot>.Fail(
                        "workspace.legacy_missing",
                        "在启动器数据目录中未找到旧的实例或版本数据。");
                }

                var root = new WorkspaceRoot(
                    CreateId(legacy.Path),
                    "旧版数据目录",
                    legacy.Path,
                    WorkspaceKind.Managed,
                    WorkspaceStatus.Ready,
                    DateTimeOffset.UtcNow);

                var saved = Save(state => state with
                {
                    PrimaryId = root.Id,
                    Roots = Replace(state.Roots, root),
                    OnboardingCompletedAtUtc = DateTimeOffset.UtcNow,
                    OnboardingSkipped = false,
                });
                if (!saved.IsSuccess)
                {
                    return Result<WorkspaceRoot>.Fail(saved.Error!);
                }

                _paths.SetGameRoot(root.Path);
                _paths.SyncInstanceRootMappings(_state.Roots, _state.PrimaryId);
                _logger.LogWarning("已按用户选择继续使用旧版数据目录作为工作区：{Path}", root.Path);
                return Result<WorkspaceRoot>.Ok(root);
            },
            cancellationToken);

    public StorageLayout GetStorageLayout() => StorageLayout.From(_paths);

    private Result Save(Func<WorkspaceFile, WorkspaceFile> update)
    {
        _gate.Wait();
        CrossProcessFileLock? fileLock = null;
        try
        {
            fileLock = CrossProcessFileLock.TryAcquire(
                _paths.Root,
                "workspace.lock",
                WorkspaceLockTimeout);

            // 跨进程锁不可用时不写入：静默覆盖其他进程的登记会产生假成功（F-02 §8.7）。
            if (fileLock is null)
            {
                _logger.LogWarning("无法获取工作区跨进程写锁（可能有其他 ECL 进程正在写入）。");
                return Result.Fail("workspace.busy", "另一个 ECL 进程正在更新工作区登记，请稍后重试。");
            }

            var latest = _store.Load(out var loadError);
            var baseline = loadError is null ? latest : _state;
            if (loadError is not null)
            {
                _logger.LogWarning("工作区配置重新加载失败，保留内存状态继续写入：{Error}", loadError);
            }

            var next = update(baseline) with { SchemaVersion = WorkspaceFile.CurrentSchemaVersion };
            _store.Save(next);
            var rootsChanged = RootsChanged(baseline, next);
            _state = next;
            if (rootsChanged)
            {
                _paths.SyncInstanceRootMappings(next.Roots, next.PrimaryId);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(exception, "写入工作区配置失败。");
            return Result.Fail("workspace.write_failed", "写入工作区配置失败。", exception.Message);
        }
        finally
        {
            fileLock?.Dispose();
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return Result.Ok();
    }

    private WorkspaceRoot? FindRootByPath(string path) =>
        _state.Roots.FirstOrDefault(root =>
            string.Equals(PathIdentity.Normalize(root.Path), PathIdentity.Normalize(path), StringComparison.OrdinalIgnoreCase));

    private WorkspaceRoot? FindConflictingRoot(WorkspacePathCheck check)
    {
        foreach (var root in _state.Roots)
        {
            if (MatchesIdentity(root, check))
            {
                return root;
            }
        }

        return null;
    }

    private static bool MatchesIdentity(WorkspaceRoot root, WorkspacePathCheck check)
    {
        if (!string.IsNullOrWhiteSpace(check.PathIdentityKey) &&
            string.Equals(root.PathIdentityKey, check.PathIdentityKey, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(check.CanonicalPath) &&
            !string.IsNullOrWhiteSpace(root.CanonicalPath) &&
            string.Equals(
                PathIdentity.Normalize(root.CanonicalPath),
                PathIdentity.Normalize(check.CanonicalPath),
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(
            PathIdentity.Normalize(root.Path),
            PathIdentity.Normalize(check.Path),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool RootsChanged(WorkspaceFile before, WorkspaceFile after) =>
        !string.Equals(before.PrimaryId, after.PrimaryId, StringComparison.Ordinal) ||
        before.Roots.Count != after.Roots.Count ||
        before.Roots.Any(root => after.Roots.Any(other =>
            string.Equals(other.Id, root.Id, StringComparison.Ordinal) &&
            (!string.Equals(other.Path, root.Path, StringComparison.OrdinalIgnoreCase) || other.Kind != root.Kind)));

    private static WorkspaceStatus MapStatus(WorkspacePathCheck check) => check.Status switch
    {
        WorkspaceStatus.Ready => WorkspaceStatus.Ready,
        WorkspaceStatus.Missing => WorkspaceStatus.Missing,
        WorkspaceStatus.NotWritable => WorkspaceStatus.NotWritable,
        WorkspaceStatus.NotAccessible => WorkspaceStatus.NotAccessible,
        WorkspaceStatus.Unavailable => WorkspaceStatus.Unavailable,
        _ => WorkspaceStatus.Invalid,
    };

    private static IReadOnlyList<WorkspaceRoot> Replace(IReadOnlyList<WorkspaceRoot> roots, WorkspaceRoot value)
    {
        var list = roots
            .Where(root => !string.Equals(root.Id, value.Id, StringComparison.Ordinal))
            .ToList();
        list.Add(value);
        return list;
    }

    private static void EnsureStructure(string path)
    {
        foreach (var name in new[] { "instances", "versions", "libraries", "assets" })
        {
            Directory.CreateDirectory(Path.Combine(path, name));
        }
    }

    private static int CountDirectories(string path)
    {
        try
        {
            return Directory.Exists(path)
                ? Directory.EnumerateDirectories(path).Take(64).Count()
                : 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    internal static string CreateId(string path)
    {
        var normalized = path.ToLowerInvariant();
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalized));
        return "ws-" + Convert.ToHexString(bytes)[..12].ToLowerInvariant();
    }

    private string CreateUniqueId(string path)
    {
        var baseId = CreateId(path);
        if (!_state.Roots.Any(root => string.Equals(root.Id, baseId, StringComparison.Ordinal)))
        {
            return baseId;
        }

        for (var suffix = 2; suffix < 100; suffix++)
        {
            var candidate = $"{baseId}-{suffix}";
            if (!_state.Roots.Any(root => string.Equals(root.Id, candidate, StringComparison.Ordinal)))
            {
                return candidate;
            }
        }

        return $"{baseId}-{Guid.NewGuid():N}"[..24];
    }

    internal static string CreateName(string path)
    {
        var name = new DirectoryInfo(path).Name;
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }
}
