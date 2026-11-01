// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.Extensions.Logging;

namespace Ecl.Core.Storage;

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

    Task<Result<WorkspaceRoot>> InitializeAsync(string path, CancellationToken cancellationToken = default);

    Task<Result<WorkspaceRoot>> RegisterExternalAsync(string path, CancellationToken cancellationToken = default);

    Task<Result<WorkspacePathCheck>> ValidatePrimaryAsync(CancellationToken cancellationToken = default);

    /// <summary>用户选择“稍后配置”。允许浏览，但在首次创建/下载/启动前仍会要求确认。</summary>
    Task SkipOnboardingAsync(CancellationToken cancellationToken = default);

    /// <summary>检测旧版布局中仍位于启动器数据目录下的实例/版本数据。</summary>
    WorkspaceLegacyInfo DetectLegacy();

    /// <summary>用户明确选择继续使用旧版数据目录（不推荐，只避免数据丢失）。</summary>
    Task<Result<WorkspaceRoot>> AdoptLegacyAsync(CancellationToken cancellationToken = default);

    StorageLayout GetStorageLayout();
}

/// <summary>
/// 工作区服务：负责选择、登记、校验与状态持久化。路径规则集中在
/// <see cref="WorkspacePathValidator"/>，UI 与 CLI 必须调用同一服务。
/// </summary>
public sealed class WorkspaceService : IWorkspaceService
{
    private readonly AppPaths _paths;
    private readonly WorkspaceStore _store;
    private readonly WorkspacePathValidator _validator;
    private readonly ILogger<WorkspaceService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WorkspaceFile _state;

    public WorkspaceService(AppPaths paths, ILogger<WorkspaceService> logger)
    {
        _paths = paths;
        _logger = logger;
        _store = new WorkspaceStore(paths.ConfigDirectory);
        _validator = new WorkspacePathValidator(paths.Root);
        _state = _store.Load(out var error);
        if (error is not null)
        {
            _logger.LogWarning("工作区配置不可用：{Error}", error);
        }
    }

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
                var existing = _state.Roots.FirstOrDefault(root =>
                    string.Equals(root.Path, check.Path, StringComparison.OrdinalIgnoreCase));

                var root = new WorkspaceRoot(
                    existing?.Id ?? CreateId(check.Path),
                    string.IsNullOrWhiteSpace(existing?.Name) ? CreateName(check.Path) : existing!.Name,
                    check.Path,
                    WorkspaceKind.Managed,
                    WorkspaceStatus.Ready,
                    DateTimeOffset.UtcNow);

                Save(state => state with
                {
                    PrimaryId = root.Id,
                    Roots = Replace(state.Roots, root),
                    OnboardingCompletedAtUtc = DateTimeOffset.UtcNow,
                    OnboardingSkipped = false,
                });

                _paths.SetGameRoot(root.Path);
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
        var check = Check(path, createIfMissing: false);
        if (!check.IsUsable)
        {
            return Result<WorkspaceRoot>.Fail(
                check.ProblemCode ?? "workspace.invalid",
                check.ProblemMessage ?? "外部实例目录不可用。");
        }

        return await Task.Run(
            () =>
            {
                var existing = _state.Roots.FirstOrDefault(root =>
                    string.Equals(root.Path, check.Path, StringComparison.OrdinalIgnoreCase));

                var root = new WorkspaceRoot(
                    existing?.Id ?? CreateId(check.Path),
                    string.IsNullOrWhiteSpace(existing?.Name) ? CreateName(check.Path) : existing!.Name,
                    check.Path,
                    WorkspaceKind.External,
                    WorkspaceStatus.Ready,
                    DateTimeOffset.UtcNow);

                Save(state => state with { Roots = Replace(state.Roots, root) });
                return Result<WorkspaceRoot>.Ok(root);
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
                    Status = check.Status,
                    LastValidatedAtUtc = DateTimeOffset.UtcNow,
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

                Save(state => state with
                {
                    PrimaryId = root.Id,
                    Roots = Replace(state.Roots, root),
                    OnboardingCompletedAtUtc = DateTimeOffset.UtcNow,
                    OnboardingSkipped = false,
                });

                _paths.SetGameRoot(root.Path);
                _logger.LogWarning("已按用户选择继续使用旧版数据目录作为工作区：{Path}", root.Path);
                return Result<WorkspaceRoot>.Ok(root);
            },
            cancellationToken);

    public StorageLayout GetStorageLayout() => StorageLayout.From(_paths);

    private void Save(Func<WorkspaceFile, WorkspaceFile> update)
    {
        _gate.Wait();
        try
        {
            var next = update(_state);
            _store.Save(next);
            _state = next;
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

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

    internal static string CreateName(string path)
    {
        var name = new DirectoryInfo(path).Name;
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }
}
