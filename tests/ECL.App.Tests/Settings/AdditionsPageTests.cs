// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.IO;
using Ecl.Additions;
using Ecl.App.Services;
using Ecl.App.Tests.Support;
using Ecl.App.ViewModels.Settings;
using Ecl.Core;
using Ecl.Core.Localization;
using Ecl.Deployment;
using Xunit;

namespace Ecl.App.Tests.Settings;

/// <summary>
/// 设置中心「扩展管理」页（settings-additions.md §3/§5/§7/§9）：
/// 真实安装记录与状态、只由显式操作读取的目录、依赖与 Core API 阻塞、
/// 禁停/移除/回滚的真实写入边界，以及不伪造权限清单或沙箱承诺。
/// </summary>
public sealed class AdditionsPageTests
{
    static AdditionsPageTests() => TestApplication.Ensure();

    [Fact]
    public void ActivatingThePageReadsOnlyLocalState()
    {
        var host = new PageHost();
        host.Service.Installed.Add(Installed("ecl.alpha"));
        host.Service.Installed.Add(Installed("ecl.beta", enabled: false));

        host.Page.Activate();

        // 进入页面不请求目录、不发起网络：目录读取必须是显式操作。
        Assert.Equal(0, host.Service.CatalogCalls);
        Assert.Equal(2, host.Page.Installed.Count);
        Assert.Equal("已安装 2 个扩展", host.Page.InstalledCountText);
        Assert.False(host.Page.CanResetDefaults);
        Assert.Empty(host.Page.SearchEntries);
        Assert.False(host.Page.Highlight("ecl.alpha"));
    }

    [Fact]
    public void EmptyStateExplainsThatNoCatalogIsBundled()
    {
        var host = new PageHost();
        host.Page.Activate();

        Assert.False(host.Page.HasInstalled);
        Assert.False(host.Page.HasAvailable);
        Assert.Contains("尚未安装任何扩展", host.Page.InstalledEmptyMessage);
        Assert.Contains("不内置官方目录", host.Page.AvailableEmptyMessage);
        Assert.Contains("尚未读取官方扩展目录", host.Page.CatalogStatus);
    }

    [Fact]
    public async Task InstalledRowsUseCatalogNamesOnlyWhenTheCatalogProvidesThem()
    {
        var host = new PageHost();
        host.Service.Installed.Add(Installed("ecl.alpha", version: "1.2.0"));
        host.Service.Catalog = Snapshot(Entry("ecl.alpha", name: "Alpha Toolkit", description: "Alpha purpose"));

        host.Page.Activate();
        Assert.Equal("ecl.alpha", Assert.Single(host.Page.Installed).Name);
        Assert.False(host.Page.Installed[0].HasDescription);

        await host.Page.CheckCatalogCommand.ExecuteAsync(null);

        var row = Assert.Single(host.Page.Installed);
        Assert.Equal("Alpha Toolkit", row.Name);
        Assert.Equal("Alpha purpose", row.Description);
        Assert.True(row.HasDescription);
        Assert.Equal("ecl.alpha", row.IdText);
        Assert.Equal("1.2.0", row.VersionText);
        Assert.Equal("功能扩展", row.TypeText);
        Assert.Contains("已启用", row.StatusText);
        Assert.Equal(AdditionRowState.Enabled, row.State);
    }

    [Fact]
    public async Task MissingRequiredDependenciesBlockEnablingWithoutWritingState()
    {
        var host = new PageHost();
        host.Service.Installed.Add(Installed("ecl.alpha", enabled: false, dependencies: ["ecl.beta"]));
        host.Page.Activate();

        var row = Assert.Single(host.Page.Installed);
        Assert.Equal(AdditionRowState.MissingDependency, row.State);
        Assert.Contains("依赖缺失", row.StatusText);
        Assert.False(row.CanToggle);
        Assert.Contains("ecl.beta", row.EnableBlockedReason);

        // 启用被阻止时只报告真实原因，不写任何状态。
        await host.Page.ToggleEnabledCommand.ExecuteAsync(row);
        Assert.Empty(host.Service.EnabledRequests);
        Assert.Equal(row.EnableBlockedReason, host.Page.StatusMessage);
    }

    [Fact]
    public void CoreApiIncompatibleInstalledRecordsCannotBeEnabled()
    {
        var host = new PageHost();
        host.Service.Installed.Add(Installed("ecl.alpha", enabled: false, coreMin: "2.0.0", coreMax: "3.0.0"));
        host.Page.Activate();

        var row = Assert.Single(host.Page.Installed);
        Assert.Equal(AdditionRowState.Incompatible, row.State);
        Assert.False(row.CanToggle);
        Assert.Contains("2.0.0", row.EnableBlockedReason);
        Assert.Contains("Core API", row.CompatibilityText);
    }

    [Fact]
    public async Task DisablingIsAllowedAndWritesOnlyTheEnabledFlag()
    {
        var host = new PageHost();
        host.Service.Installed.Add(Installed("ecl.alpha"));
        host.Page.Activate();
        var row = Assert.Single(host.Page.Installed);

        await host.Page.ToggleEnabledCommand.ExecuteAsync(row);

        Assert.Equal(new[] { (AdditionId.Parse("ecl.alpha"), false) }, host.Service.EnabledRequests);
        Assert.False(Assert.Single(host.Page.Installed).IsEnabled);
        Assert.Contains("已禁用", host.Page.StatusMessage);
    }

    [Fact]
    public async Task RemovalIsBlockedWhileAnotherInstalledAdditionDependsOnIt()
    {
        var host = new PageHost();
        host.Service.Installed.Add(Installed("ecl.beta"));
        host.Service.Installed.Add(Installed("ecl.alpha", enabled: false, dependencies: ["ecl.beta"]));
        host.Page.Activate();

        var beta = host.Page.Installed.Single(row => row.IdText == "ecl.beta");
        Assert.False(beta.CanRemove);
        Assert.Contains("ecl.alpha", beta.RemoveBlockedReason);

        await host.Page.RemoveCommand.ExecuteAsync(beta);
        Assert.Equal(0, host.Service.UninstallCalls);
        Assert.Equal(beta.RemoveBlockedReason, host.Page.StatusMessage);
    }

    [Fact]
    public async Task RemovalConfirmsFirstAndCallsTheDomainUninstall()
    {
        var host = new PageHost();
        host.Service.Installed.Add(Installed("ecl.alpha"));
        host.Page.Activate();
        var row = Assert.Single(host.Page.Installed);

        host.Dialogs.ConfirmResult = false;
        await host.Page.RemoveCommand.ExecuteAsync(row);
        Assert.Equal(0, host.Service.UninstallCalls);

        host.Dialogs.ConfirmResult = true;
        await host.Page.RemoveCommand.ExecuteAsync(row);
        Assert.Equal(1, host.Service.UninstallCalls);
        Assert.Empty(host.Page.Installed);
        Assert.Contains("已移除", host.Page.StatusMessage);
    }

    [Fact]
    public async Task RollbackIsOfferedOnlyWithAPreviousVersionAndConfirmsFirst()
    {
        var host = new PageHost();
        host.Service.Installed.Add(Installed("ecl.alpha", previous: "0.9.0"));
        host.Page.Activate();
        var row = Assert.Single(host.Page.Installed);
        Assert.True(row.CanRollback);
        Assert.Contains("0.9.0", row.PreviousVersionText);

        host.Dialogs.ConfirmResult = false;
        await host.Page.RollbackCommand.ExecuteAsync(row);
        Assert.Equal(0, host.Service.RollbackCalls);

        host.Dialogs.ConfirmResult = true;
        await host.Page.RollbackCommand.ExecuteAsync(row);
        Assert.Equal(1, host.Service.RollbackCalls);
        Assert.Equal("0.9.0", Assert.Single(host.Page.Installed).VersionText);
    }

    [Fact]
    public void CorruptStateFileKeepsThePageReadOnly()
    {
        var host = new PageHost();
        host.Service.Installed.Add(Installed("ecl.alpha"));
        host.Service.Corrupt = true;
        host.Service.CorruptError = "扩展状态文件损坏：unexpected end of JSON";
        host.Page.Activate();

        Assert.True(host.Page.IsStateCorrupt);
        Assert.Contains("unexpected end of JSON", host.Page.StateError);
        var row = Assert.Single(host.Page.Installed);
        Assert.False(row.CanToggle);
        Assert.False(row.CanRemove);
        Assert.Contains("状态文件损坏", row.EnableBlockedReason);
    }

    [Fact]
    public async Task CatalogIsReadOnlyOnExplicitCheckAndListsAddableEntries()
    {
        var host = new PageHost();
        host.Service.Installed.Add(Installed("ecl.installed"));
        host.Service.Catalog = Snapshot(
            Entry("ecl.installed", name: "Installed"),
            Entry("ecl.withdrawn", withdrawn: true),
            Entry(
                "ecl.alpha",
                name: "Alpha",
                description: "Alpha purpose",
                size: 3 * 1024 * 1024,
                dependencies: [Dependency("ecl.beta", "1.2.0")],
                platforms: ["windows"]),
            Entry("ecl.beta", name: "Beta", coreMax: "2.0.0"));

        host.Page.Activate();
        Assert.Empty(host.Page.Available);

        await host.Page.CheckCatalogCommand.ExecuteAsync(null);

        Assert.Equal(1, host.Service.CatalogCalls);
        // 已安装与已撤回的条目不会出现在“可安装”列表里。
        Assert.Equal(["ecl.alpha", "ecl.beta"], host.Page.Available.Select(row => row.IdText));
        var alpha = host.Page.Available.Single(row => row.IdText == "ecl.alpha");
        Assert.Equal("Alpha", alpha.Name);
        Assert.Equal("3 MB", alpha.SizeText);
        Assert.Contains("ecl.beta 1.2.0+", alpha.DependenciesText);
        Assert.Contains("1.0.0", alpha.CompatibilityText);
        Assert.Contains("windows", alpha.PlatformsText);
        Assert.Contains("签名验证", host.Page.CatalogStatus);
        Assert.Contains("2 个", host.Page.AvailableCountText);
    }

    [Fact]
    public async Task CatalogFailuresKeepTheRealReasonInsteadOfAnEmptyCatalog()
    {
        var host = new PageHost();
        host.Service.CatalogError = new EclError(
            "additions.catalog_not_configured",
            "当前版本没有内置官方扩展目录，也没有配置目录地址。");

        await host.Page.CheckCatalogCommand.ExecuteAsync(null);

        Assert.Empty(host.Page.Available);
        // 已知错误码走本地化文案；不会显示为“目录为空”。
        Assert.Contains("没有内置官方扩展目录", host.Page.CatalogStatus);
        Assert.DoesNotContain("目录中没有可安装", host.Page.CatalogStatus);
    }

    [Fact]
    public async Task CachedCatalogIsMarkedAsPossiblyOutdated()
    {
        var host = new PageHost();
        host.Service.Catalog = CachedSnapshot(
            Entry("ecl.alpha", name: "Alpha"),
            "使用本地缓存的扩展目录。");

        await host.Page.CheckCatalogCommand.ExecuteAsync(null);

        Assert.Contains("缓存", host.Page.CatalogStatus);
        Assert.Contains("使用本地缓存的扩展目录", host.Page.CatalogStatus);
    }

    [Fact]
    public async Task PlanPreviewShowsRealEntriesAndRequiresConfirmationBeforeInstalling()
    {
        var host = new PageHost();
        host.Service.Catalog = Snapshot(Entry("ecl.alpha", name: "Alpha", size: 4096));

        await host.Page.CheckCatalogCommand.ExecuteAsync(null);
        await host.Page.PlanCommand.ExecuteAsync("ecl.alpha");

        Assert.NotNull(host.Page.Plan);
        var item = Assert.Single(host.Page.Plan!.Items);
        Assert.Equal("安装", item.KindText);
        Assert.Equal("Alpha", item.Title);
        Assert.Equal("4 KB", item.SizeText);
        Assert.Equal("将安装 1 个、更新 0 个扩展。", host.Page.Plan.SummaryText);
        Assert.Equal(0, host.Service.ApplyCalls);

        host.Dialogs.ConfirmResult = false;
        await host.Page.ConfirmPlanCommand.ExecuteAsync(null);
        Assert.Equal(0, host.Service.ApplyCalls);
        Assert.Contains("已取消", host.Page.StatusMessage);

        host.Dialogs.ConfirmResult = true;
        await host.Page.ConfirmPlanCommand.ExecuteAsync(null);
        Assert.Equal(1, host.Service.ApplyCalls);
        Assert.Null(host.Page.Plan);
        Assert.Equal("Alpha", Assert.Single(host.Page.Installed).Name);
        Assert.Contains("已完成安装或更新", host.Page.StatusMessage);
    }

    [Fact]
    public async Task PlanRequiresTheCatalogAndReportsAlreadyCurrentTargets()
    {
        var host = new PageHost();
        await host.Page.PlanCommand.ExecuteAsync("ecl.alpha");
        Assert.Contains("检查目录更新", host.Page.StatusMessage);
        Assert.Equal(0, host.Service.PlanCalls);

        host.Service.Catalog = Snapshot(Entry("ecl.alpha", version: "1.0.0"));
        host.Service.Installed.Add(Installed("ecl.alpha", version: "1.0.0"));
        await host.Page.CheckCatalogCommand.ExecuteAsync(null);
        await host.Page.PlanCommand.ExecuteAsync("ecl.alpha");

        Assert.Null(host.Page.Plan);
        Assert.Contains("已安装且无需更新", host.Page.StatusMessage);
    }

    [Fact]
    public async Task UpdatesAreOfferedWhenTheCatalogHasANewerVersion()
    {
        var host = new PageHost();
        host.Service.Installed.Add(Installed("ecl.alpha", version: "1.0.0"));
        host.Service.Catalog = Snapshot(Entry("ecl.alpha", version: "1.1.0", name: "Alpha"));

        await host.Page.CheckCatalogCommand.ExecuteAsync(null);

        var row = Assert.Single(host.Page.Installed);
        Assert.True(row.HasUpdate);
        Assert.True(row.CanUpdate);
        Assert.Contains("1.1.0", row.UpdateText);

        await host.Page.PlanCommand.ExecuteAsync("ecl.alpha");
        Assert.Equal("更新", Assert.Single(host.Page.Plan!.Items).KindText);
    }

    [Fact]
    public async Task SearchAndStatusFiltersOnlyNarrowTheInstalledTable()
    {
        var host = new PageHost();
        host.Service.Installed.Add(Installed("ecl.alpha", enabled: true));
        host.Service.Installed.Add(Installed("ecl.beta", enabled: false));
        host.Service.Catalog = Snapshot(Entry("ecl.available", name: "Gamma"));
        await host.Page.CheckCatalogCommand.ExecuteAsync(null);
        host.Page.Activate();

        host.Page.FilterText = "alpha";
        Assert.Equal(["ecl.alpha"], host.Page.Installed.Select(row => row.IdText));
        Assert.Single(host.Page.Available);

        host.Page.FilterText = string.Empty;
        host.Page.SelectedStatusFilter = host.Page.StatusFilters.Single(
            filter => filter.Value == AdditionStatusFilterValues.Disabled);
        Assert.Equal(["ecl.beta"], host.Page.Installed.Select(row => row.IdText));

        host.Page.SelectedStatusFilter = host.Page.StatusFilters.Single(
            filter => filter.Value == AdditionStatusFilterValues.MissingDependency);
        Assert.Empty(host.Page.Installed);
        Assert.True(host.Page.IsInstalledFilteredEmpty);
        Assert.Contains("没有符合", host.Page.InstalledFilteredEmptyMessage);
    }

    [Fact]
    public async Task DomainFailuresAreReportedInsteadOfSuccess()
    {
        var host = new PageHost();
        host.Service.Installed.Add(Installed("ecl.alpha"));
        host.Service.SetEnabledError = new EclError("additions.state_write_failed", "写入扩展状态失败。");
        host.Page.Activate();

        await host.Page.ToggleEnabledCommand.ExecuteAsync(Assert.Single(host.Page.Installed));

        Assert.Equal("写入扩展状态失败。", host.Page.StatusMessage);
        Assert.True(Assert.Single(host.Page.Installed).IsEnabled);
    }

    [Fact]
    public void PermissionAndSandboxBoundariesAreDeclaredInsteadOfFaked()
    {
        var host = new PageHost();
        host.Page.Activate();

        Assert.Equal(4, host.Page.Boundaries.Items.Count);
        Assert.Contains("ADR 0053", host.Page.Boundaries.Items[0].Reason);
        Assert.Contains("沙箱", host.Page.Boundaries.Items[1].Title);
        Assert.Contains("ADR 0054", host.Page.Boundaries.Items[2].Reason);
        Assert.Contains("发布者", host.Page.Boundaries.Items[3].Title);

        // 安装确认不冒充 ADR 0053 的“允许并安装”授权，也不宣称已获得权限。
        Assert.Contains("尚未实现", host.Page.AuthorizationNote);
        Assert.Contains("不宣称扩展已被沙箱隔离", host.Page.AuthorizationNote);
    }

    [Fact]
    public void StatusFilterOptionsExposeLocalizedNamesToAutomation()
    {
        var host = new PageHost();

        Assert.All(host.Page.StatusFilters, filter => Assert.Equal(filter.DisplayName, filter.ToString()));
        Assert.Equal("全部状态", host.Page.StatusFilters[0].DisplayName);
    }

    private static InstalledAddition Installed(
        string id,
        string version = "1.0.0",
        bool enabled = true,
        string[]? dependencies = null,
        string? coreMin = null,
        string? coreMax = null,
        string? previous = null,
        AdditionType type = AdditionType.Feature) =>
        new(
            AdditionId.Parse(id),
            type,
            ReleaseVersion.Parse(version),
            Path.Combine(Path.GetTempPath(), "ecl-additions-tests", id, version),
            "Ecl.TestAddition.dll",
            enabled,
            DataSchemaVersion: 1,
            Files: [],
            DateTimeOffset.Parse("2026-11-01T10:00:00Z", CultureInfo.InvariantCulture),
            previous is null ? null : ReleaseVersion.Parse(previous))
        {
            Dependencies = dependencies?.Select(AdditionId.Parse).ToArray() ?? [],
            CoreApiMin = coreMin is null ? null : ReleaseVersion.Parse(coreMin),
            CoreApiMax = coreMax is null ? null : ReleaseVersion.Parse(coreMax),
        };

    private static AdditionEntry Entry(
        string id,
        string version = "1.0.0",
        string? name = null,
        string? description = null,
        long size = 2048,
        IReadOnlyList<AdditionDependency>? dependencies = null,
        string coreMin = "1.0.0",
        string coreMax = "9.9.9",
        IReadOnlyList<string>? platforms = null,
        bool withdrawn = false) =>
        new(
            AdditionId.Parse(id),
            AdditionType.Feature,
            name ?? id,
            description ?? "test addition",
            ReleaseVersion.Parse(version),
            new AdditionPackage($"https://example.invalid/{id}-{version}.zip", size, new string('a', 64)),
            new AdditionCoreApiRange(ReleaseVersion.Parse(coreMin), ReleaseVersion.Parse(coreMax)),
            dependencies ?? [],
            [],
            DataSchemaVersion: null,
            platforms ?? [],
            withdrawn);

    private static AdditionDependency Dependency(string id, string minVersion) =>
        new(AdditionId.Parse(id), ReleaseVersion.Parse(minVersion));

    private static AdditionCatalogSnapshot Snapshot(params AdditionEntry[] entries) =>
        BuildSnapshot(entries, fromCache: false, warnings: []);

    private static AdditionCatalogSnapshot CachedSnapshot(AdditionEntry entry, params string[] warnings) =>
        BuildSnapshot([entry], fromCache: true, warnings);

    private static AdditionCatalogSnapshot BuildSnapshot(
        AdditionEntry[] entries,
        bool fromCache,
        IReadOnlyList<string> warnings) =>
        new(
            new AdditionCatalog(
                AdditionCatalogParser.CurrentSchemaVersion,
                AdditionCoreApi.Version,
                entries),
            fromCache,
            DateTimeOffset.UtcNow,
            warnings);

    private sealed class PageHost
    {
        public PageHost()
        {
            Dialogs = new FakeDialogService();
            Service = new FakeAdditionsService();
            Page = new SettingsAdditionsPageViewModel(
                Service,
                Dialogs,
                new JsonLocalizer(CultureInfo.GetCultureInfo("zh-CN")));
        }

        public FakeDialogService Dialogs { get; }

        public FakeAdditionsService Service { get; }

        public SettingsAdditionsPageViewModel Page { get; }
    }


    private sealed class FakeDialogService : IDialogService
    {
        public bool ConfirmResult { get; set; } = true;

        public bool Confirm(string title, string message, string? acknowledgementToken = null) => ConfirmResult;

        public InstanceDeletionChoice ChooseInstanceDeletion(
            string title,
            string message,
            bool canDeleteManagedData) => InstanceDeletionChoice.Cancel;

        public void ShowInfo(string title, string message)
        {
        }

        public void ShowError(string title, string message)
        {
        }

        public string? PickOpenFile(string title, string filter) => null;

        public IReadOnlyList<string> PickOpenFiles(string title, string filter) => [];

        public string? PickSaveFile(string title, string filter, string? defaultFileName = null) => null;

        public string? PickFolder(string title) => null;

        public bool OpenUrl(string url) => false;
    }
}
