// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.IO;
using Ecl.Additions;
using Ecl.App.Services;
using Ecl.App.Tests.Support;
using Ecl.App.ViewModels;
using Ecl.Configuration.Settings;
using Ecl.Core;
using Ecl.Core.Localization;
using Xunit;

namespace Ecl.App.Tests.About;

/// <summary>
/// 关于页面（hints/ui/pages/about.md）：版本/频道/架构来自真实构建信息；更新检查只把
/// <see cref="IUpdateCheckService"/> 的真实结论映射成文案；项目协作保持原生项目中心语义；
/// 外部链接与第三方许可证文本的失败路径可观察。
/// </summary>
public sealed class AboutPageTests
{
    [Fact]
    public void VersionAndChannelComeFromRealBuildMetadata()
    {
        var host = new Host();

        Assert.Equal(BuildInfo.Version, host.Page.VersionText);
        Assert.False(string.IsNullOrWhiteSpace(host.Page.ArchitectureText));
        Assert.Contains("Stable", host.Page.ChannelText);
        Assert.Contains("main", host.Page.ChannelText);
        Assert.Contains("尚未检查更新", host.Page.UpdateStatusText);
        Assert.True(host.Page.CanCheckForUpdates);
        Assert.True(host.Page.IsUpdateCheckButtonEnabled);
    }

    [Fact]
    public void ChannelAliasIsShownFromTheConfiguredSetting()
    {
        var host = new Host();
        host.Settings.Values["launcher.updateChannel"] = "dev";

        host.Page.Activate();

        Assert.Contains("Beta", host.Page.ChannelText);
        Assert.Contains("dev", host.Page.ChannelText);
    }

    [Fact]
    public async Task DisabledUpdateCheckDoesNotCallTheService()
    {
        var host = new Host();
        host.Settings.Values["launcher.checkUpdates"] = "false";
        host.Page.Activate();

        Assert.False(host.Page.CanCheckForUpdates);
        Assert.False(host.Page.IsUpdateCheckButtonEnabled);
        Assert.Contains("关闭", host.Page.UpdateStatusText);

        await host.Page.CheckForUpdatesCommand.ExecuteAsync(null);

        Assert.Equal(0, host.Updates.Calls);
    }

    [Fact]
    public async Task AvailableUpdateShowsVersionAndChannelWithoutPretendingToInstall()
    {
        var host = new Host();
        host.Updates.Reports.Enqueue(new UpdateCheckReport(
            UpdateCheckOutcome.UpdateAvailable,
            "dev",
            "0.8.0",
            "0.9.0",
            "release notes",
            DateTimeOffset.Parse("2026-11-15T00:00:00Z", CultureInfo.InvariantCulture),
            null,
            null));

        await host.Page.CheckForUpdatesCommand.ExecuteAsync(null);

        Assert.Equal(1, host.Updates.Calls);
        Assert.Contains("0.9.0", host.Page.UpdateStatusText);
        Assert.Contains("Beta", host.Page.UpdateStatusText);
        Assert.True(host.Page.HasUpdateDetail);
        Assert.Contains("Bootstrapper", host.Page.UpdateDetailText);
        Assert.True(host.Page.CanCheckForUpdates);
        Assert.False(host.Page.IsCheckingUpdates);
    }

    [Fact]
    public async Task LatestVersionIsReportedAsSuch()
    {
        var host = new Host();
        host.Updates.Reports.Enqueue(new UpdateCheckReport(
            UpdateCheckOutcome.UpToDate,
            "main",
            "0.8.0",
            "0.8.0",
            null,
            null,
            null,
            null));

        await host.Page.CheckForUpdatesCommand.ExecuteAsync(null);

        Assert.Contains("最新", host.Page.UpdateStatusText);
        Assert.False(host.Page.HasUpdateDetail);
    }

    [Fact]
    public async Task UnavailableCheckExplainsTheMissingPrerequisiteAndStaysRetryable()
    {
        var host = new Host();
        host.Updates.Reports.Enqueue(new UpdateCheckReport(
            UpdateCheckOutcome.Unavailable,
            "main",
            "0.8.0",
            null,
            null,
            null,
            "update.feed_not_configured",
            "未配置更新源。"));

        await host.Page.CheckForUpdatesCommand.ExecuteAsync(null);

        Assert.Contains("未配置更新源", host.Page.UpdateStatusText);
        Assert.Contains("ECL_UPDATE_FEED", host.Page.UpdateDetailText);
        Assert.True(host.Page.CanCheckForUpdates);
        Assert.True(host.Page.IsUpdateCheckButtonEnabled);
    }

    [Fact]
    public async Task FailedCheckKeepsTheFailureReasonVisible()
    {
        var host = new Host();
        host.Updates.Reports.Enqueue(new UpdateCheckReport(
            UpdateCheckOutcome.Failed,
            "main",
            "0.8.0",
            null,
            null,
            null,
            "update.check_failed",
            "网络不可达"));

        await host.Page.CheckForUpdatesCommand.ExecuteAsync(null);

        Assert.Contains("网络不可达", host.Page.UpdateStatusText);
        Assert.True(host.Page.IsUpdateCheckButtonEnabled);
    }

    [Fact]
    public void ProjectCenterStateFollowsRealAdditionRecords()
    {
        var host = new Host();
        Assert.Contains("未安装", host.Page.ProjectCenterStatusText);

        host.Service.Installed.Add(Installed("ecl.github-project-center", enabled: true));
        host.Page.Activate();
        Assert.Contains("已启用", host.Page.ProjectCenterStatusText);

        host.Service.Installed[0] = Installed("ecl.github-project-center", enabled: false);
        host.Page.Activate();
        Assert.Contains("已禁用", host.Page.ProjectCenterStatusText);

        host.Service.Corrupt = true;
        host.Page.Activate();
        Assert.Contains("损坏", host.Page.ProjectCenterStatusText);
        Assert.Contains("ecl.github-project-center", host.Page.ProjectCenterRequirementText);
    }

    [Fact]
    public void ProjectCenterManagementUsesTheShellProvidedEntry()
    {
        var host = new Host();
        var invoked = 0;
        host.Page.OpenAdditionsManagement = () => invoked++;

        host.Page.OpenProjectCenterManagementCommand.Execute(null);

        Assert.Equal(1, invoked);
    }

    [Fact]
    public void ProjectLinksOpenConfirmedOfficialResources()
    {
        var host = new Host();

        host.Page.OpenRepositoryCommand.Execute(null);
        host.Page.OpenDocumentationCommand.Execute(null);
        host.Page.OpenLicenseCommand.Execute(null);
        host.Page.OpenContributorsCommand.Execute(null);

        Assert.Contains("https://github.com/hellokug2699/Ender-Craft-Launcher", host.Links.Opened);
        Assert.Contains("https://github.com/hellokug2699/Ender-Craft-Launcher/tree/nightly/docs", host.Links.Opened);
        Assert.Contains("https://github.com/hellokug2699/Ender-Craft-Launcher/blob/nightly/LICENSE", host.Links.Opened);
        Assert.Contains("https://github.com/hellokug2699/Ender-Craft-Launcher/graphs/contributors", host.Links.Opened);
        Assert.Empty(host.Page.StatusMessage);
    }

    [Fact]
    public void FailedLinkOpenIsReportedWithoutCrashingThePage()
    {
        var host = new Host();
        host.Links.Result = false;

        host.Page.OpenRepositoryCommand.Execute(null);

        Assert.NotEmpty(host.Page.StatusMessage);
        Assert.True(host.Page.HasStatusMessage);
    }

    [Fact]
    public void ThirdPartyLicenseTextOpensTheBundledFileAndMissingTextIsMarked()
    {
        var host = new Host();
        var component = Assert.Single(host.Page.ThirdPartyComponents);
        Assert.Contains("HarmonyOS Sans", component.Name);
        Assert.True(component.HasLicenseText);

        host.Page.OpenThirdPartyLicenseCommand.Execute(component);
        Assert.Contains(component.LicensePath, host.Links.Opened);

        var missing = new ThirdPartyComponentViewModel(
            "Component",
            "未声明",
            "License",
            "Copyright",
            "missing.txt",
            hasLicenseText: false);
        host.Page.StatusMessage = string.Empty;
        host.Page.OpenThirdPartyLicenseCommand.Execute(missing);
        Assert.NotEmpty(host.Page.StatusMessage);
    }

    [Fact]
    public void ContributorsUseACollectiveNameWithoutInventedHeadcounts()
    {
        var host = new Host();

        Assert.Equal("ECL Contributors", host.Page.ContributorsName);
        Assert.DoesNotContain("Contributors:", host.Page.ContributorsName);
    }

    [Fact]
    public void TheViewContainsTheApprovedSingleColumnSections()
    {
        var xaml = ReadSource("src/ECL.App/Views/AboutView.xaml");

        foreach (var key in new[]
                 {
                     "about.version.section",
                     "about.links.section",
                     "about.license.section",
                     "about.thirdParty.section",
                     "about.contributors.section",
                     "about.disclaimer",
                     "CheckForUpdatesCommand",
                     "OpenThirdPartyLicenseCommand",
                 })
        {
            Assert.Contains(key, xaml, StringComparison.Ordinal);
        }

        Assert.Contains("ThirdPartyComponentTemplate", xaml, StringComparison.Ordinal);
        // 关于页不使用横向 Tab，也不暴露私有 Core 仓库或数据目录。
        Assert.DoesNotContain("<TabControl", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Ender-Craft-Launcher-Core", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("DataDirectory", xaml, StringComparison.Ordinal);
    }

    private static InstalledAddition Installed(string id, bool enabled) =>
        new(
            AdditionId.Parse(id),
            AdditionType.Feature,
            Ecl.Deployment.ReleaseVersion.Parse("1.0.0"),
            Path.Combine(Path.GetTempPath(), "ecl-about-tests", id),
            "Ecl.TestAddition.dll",
            enabled,
            DataSchemaVersion: 1,
            Files: [],
            DateTimeOffset.Parse("2026-11-01T10:00:00Z", CultureInfo.InvariantCulture));

    private sealed class Host
    {
        public Host()
        {
            Settings = new FakeSettingsService();
            Service = new FakeAdditionsService();
            Updates = new FakeUpdateCheckService();
            Links = new FakeExternalLinkService();
            Page = new AboutViewModel(
                Updates,
                Settings,
                Service,
                Links,
                new JsonLocalizer(CultureInfo.GetCultureInfo("zh-CN")));
        }

        public FakeSettingsService Settings { get; }

        public FakeAdditionsService Service { get; }

        public FakeUpdateCheckService Updates { get; }

        public FakeExternalLinkService Links { get; }

        public AboutViewModel Page { get; }
    }

    private sealed class FakeUpdateCheckService : IUpdateCheckService
    {
        public Queue<UpdateCheckReport> Reports { get; } = new();

        public int Calls { get; private set; }

        public Task<UpdateCheckReport> CheckAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(Reports.Count > 0
                ? Reports.Dequeue()
                : new UpdateCheckReport(
                    UpdateCheckOutcome.UpToDate,
                    "main",
                    "0.8.0",
                    "0.8.0",
                    null,
                    null,
                    null,
                    null));
        }
    }

    private sealed class FakeExternalLinkService : IExternalLinkService
    {
        public List<string> Opened { get; } = [];

        public bool Result { get; set; } = true;

        public bool Open(string target)
        {
            Opened.Add(target);
            return Result;
        }
    }

    private static string ReadSource(string relativePath)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "ECL.sln")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"找不到页面源文件：{path}");
        return File.ReadAllText(path);
    }
}
