// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO;
using Ecl.Additions;
using Ecl.Core;
using Ecl.Deployment;
using Ecl.Download;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ecl.App.Services;

/// <summary>
/// 设置中心「扩展管理」页的领域入口：把已安装状态、目录获取与安装/启停/移除/回滚收敛到一处，
/// 页面只做呈现与确认，不自行读写 <c>additions/</c>、不解析目录 JSON、不构造包地址。
///
/// 目录读取沿用 ADR 0032 的既有边界：地址只来自 <c>ECL_ADDITIONS_CATALOG</c>（HTTPS），
/// 签名公钥来自随包提供的 <c>additions-trust.json</c>；目录、签名或缓存不通过校验时如实失败。
/// 页面与这里都不声明扩展能力授权：权限执行属于 ADR 0053，当前实现尚未开始。
/// </summary>
public interface IAdditionsService
{
    /// <summary>读取本机已安装扩展记录；文件损坏时返回损坏标记，不猜测内容。</summary>
    AdditionStateLoad LoadState();

    /// <summary>
    /// 读取官方扩展目录。<paramref name="refresh"/> 为 <c>true</c> 时忽略进程内缓存重新请求；
    /// 未配置地址、缺少信任信息或签名不通过时返回稳定错误码，不降级为“空目录”。
    /// </summary>
    Task<Result<AdditionCatalogSnapshot>> GetCatalogAsync(bool refresh = false, CancellationToken cancellationToken = default);

    /// <summary>按目录与已安装记录生成安装/更新计划；依赖、Core API、平台与冲突检查都在领域层。</summary>
    Task<Result<AdditionInstallPlan>> CreatePlanAsync(
        IReadOnlyList<AdditionId> targets,
        CancellationToken cancellationToken = default);

    /// <summary>按计划给出的顺序安装/更新；任一项失败即停止并返回该失败。</summary>
    Task<Result> ApplyPlanAsync(AdditionInstallPlan plan, CancellationToken cancellationToken = default);

    /// <summary>只改写启用标记，不删除扩展文件或用户数据。</summary>
    Task<Result> SetEnabledAsync(AdditionId id, bool enabled, CancellationToken cancellationToken = default);

    /// <summary>移除扩展包与记录；用户数据目录保留（ADR 0032 §7）。</summary>
    Task<Result> UninstallAsync(AdditionId id, CancellationToken cancellationToken = default);

    /// <summary>回滚到安装记录中的上一版本（只在该版本目录仍存在时可用）。</summary>
    Task<Result<InstalledAddition>> RollbackAsync(AdditionId id, CancellationToken cancellationToken = default);
}

public sealed class AdditionsService : IAdditionsService
{
    private static readonly TimeSpan CatalogTimeout = TimeSpan.FromSeconds(20);

    private readonly IAppPaths _paths;
    private readonly IHttpClientProvider _http;
    private readonly ILogger<AdditionsService> _logger;
    private readonly AdditionStateStore _state;
    private readonly AdditionInstaller _installer;
    private readonly AdditionPlanner _planner = new();

    private AdditionCatalogSnapshot? _catalog;

    public AdditionsService(
        IAppPaths paths,
        IDownloadService download,
        IHttpClientProvider http,
        ILogger<AdditionsService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(download);
        ArgumentNullException.ThrowIfNull(http);

        _paths = paths;
        _http = http;
        _logger = logger ?? NullLogger<AdditionsService>.Instance;

        var root = Path.Combine(paths.Root, "additions");
        _state = new AdditionStateStore(Path.Combine(root, "installed.json"));
        _installer = new AdditionInstaller(
            new DownloadAdditionPackageProvider(download, Path.Combine(root, "tmp")),
            _state,
            root,
            null);
    }

    public AdditionStateLoad LoadState() => _state.Load();

    public async Task<Result<AdditionCatalogSnapshot>> GetCatalogAsync(
        bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        if (!refresh && _catalog is not null)
        {
            return Result<AdditionCatalogSnapshot>.Ok(_catalog);
        }

        var configured = ReadCatalogUris();
        if (!configured.IsSuccess)
        {
            return Result<AdditionCatalogSnapshot>.Fail(configured.Error!);
        }

        var trust = await AdditionCatalogService
            .LoadTrustAsync(
                Path.Combine(AppContext.BaseDirectory, AdditionCatalogService.TrustFileName),
                cancellationToken)
            .ConfigureAwait(false);
        if (!trust.IsSuccess)
        {
            return Result<AdditionCatalogSnapshot>.Fail(trust.Error!);
        }

        using var http = _http.CreateClient();
        http.Timeout = CatalogTimeout;
        var service = new AdditionCatalogService(
            new HttpAdditionCatalogSource(http, configured.Value!.Catalog, configured.Value.Signature),
            new AdditionCatalogStore(Path.Combine(_paths.Root, "additions", "catalog")),
            trust.Value!);

        var snapshot = await service.GetAsync(allowCache: true, cancellationToken).ConfigureAwait(false);
        if (!snapshot.IsSuccess)
        {
            return snapshot;
        }

        _catalog = snapshot.Value!;
        _logger.LogInformation(
            "扩展目录已载入（缓存={FromCache}，条目={Count}）。",
            _catalog.FromCache,
            _catalog.Catalog.Additions.Count);
        return snapshot;
    }

    public Task<Result<AdditionInstallPlan>> CreatePlanAsync(
        IReadOnlyList<AdditionId> targets,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targets);

        var load = _state.Load();
        if (load.IsCorrupt)
        {
            return Task.FromResult(Result<AdditionInstallPlan>.Fail(
                "additions.state_corrupt",
                "扩展状态文件损坏，无法生成安装计划。",
                load.Error));
        }

        if (_catalog is null)
        {
            return Task.FromResult(Result<AdditionInstallPlan>.Fail(
                "additions.catalog_not_loaded",
                "尚未读取官方扩展目录，无法生成安装计划。"));
        }

        return Task.FromResult(_planner.Plan(
            _catalog.Catalog,
            load.Document.Additions,
            targets,
            AdditionCoreApi.Version,
            CurrentPlatform()));
    }

    public async Task<Result> ApplyPlanAsync(
        AdditionInstallPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        foreach (var item in plan.All)
        {
            var installed = await _installer.InstallAsync(item.Entry, cancellationToken).ConfigureAwait(false);
            if (!installed.IsSuccess)
            {
                return Result.Fail(installed.Error!);
            }

            _logger.LogInformation("扩展 {Addition} {Version} 已完成安装或更新。", item.Entry.Id.Value, item.Entry.Version);
        }

        return Result.Ok();
    }

    public Task<Result> SetEnabledAsync(
        AdditionId id,
        bool enabled,
        CancellationToken cancellationToken = default) =>
        _installer.SetEnabledAsync(id, enabled, cancellationToken);

    public Task<Result> UninstallAsync(AdditionId id, CancellationToken cancellationToken = default) =>
        _installer.UninstallAsync(id, force: false, cancellationToken);

    public Task<Result<InstalledAddition>> RollbackAsync(
        AdditionId id,
        CancellationToken cancellationToken = default) =>
        _installer.RollbackAsync(id, cancellationToken);

    /// <summary>目录地址只允许显式配置的 HTTPS 绝对地址；未配置时如实报告，不构造在线目录。</summary>
    private Result<CatalogUris> ReadCatalogUris()
    {
        var catalogValue = Environment.GetEnvironmentVariable("ECL_ADDITIONS_CATALOG");
        if (string.IsNullOrWhiteSpace(catalogValue))
        {
            return Result<CatalogUris>.Fail(
                "additions.catalog_not_configured",
                "当前版本没有内置官方扩展目录，也没有配置目录地址。");
        }

        if (!Uri.TryCreate(catalogValue, UriKind.Absolute, out var catalog) ||
            !string.Equals(catalog.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return Result<CatalogUris>.Fail(
                "additions.catalog_url_invalid",
                "扩展目录地址必须是 HTTPS 绝对地址。");
        }

        var signatureValue = Environment.GetEnvironmentVariable("ECL_ADDITIONS_CATALOG_SIGNATURE");
        if (string.IsNullOrWhiteSpace(signatureValue))
        {
            return Result<CatalogUris>.Ok(new CatalogUris(catalog, new Uri(catalog.AbsoluteUri + ".sig")));
        }

        if (!Uri.TryCreate(signatureValue, UriKind.Absolute, out var signature) ||
            !string.Equals(signature.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return Result<CatalogUris>.Fail(
                "additions.catalog_signature_url_invalid",
                "扩展目录签名地址必须是 HTTPS 绝对地址。");
        }

        return Result<CatalogUris>.Ok(new CatalogUris(catalog, signature));
    }

    private static string CurrentPlatform() => OperatingSystem.IsWindows()
        ? "windows"
        : OperatingSystem.IsLinux()
            ? "linux"
            : "macos";

    private sealed record CatalogUris(Uri Catalog, Uri Signature);
}
