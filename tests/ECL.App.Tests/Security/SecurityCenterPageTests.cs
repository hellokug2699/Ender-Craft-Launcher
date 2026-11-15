// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.IO;
using Ecl.App.Dialogs;
using Ecl.App.Tests.Support;
using Ecl.App.ViewModels.Security;
using Ecl.Core.Localization;
using Ecl.Minecraft.Instances;
using Ecl.Minecraft.Security;
using Xunit;

namespace Ecl.App.Tests.Security;

/// <summary>
/// 安全中心（hints/ui/pages/security-center.md）：手动扫描、按扫描根分类、覆盖/完整性、
/// 忽略只写风险接受记录、删除走领域隔离；页面不显示文件路径，也不读取历史扫描。
/// </summary>
public sealed class SecurityCenterPageTests
{
    [Fact]
    public void TheInitialStateIsBlueAndNotScanned()
    {
        var host = new Host();

        Assert.Equal(SecurityOverallState.NotScanned, host.Page.OverallState);
        Assert.Equal("blue", host.Page.StatusKey);
        Assert.True(host.Page.CanStartScan);
        Assert.False(host.Page.ShowSolveButton);
        Assert.True(host.Page.ShowNotScannedHint);
        Assert.Empty(host.Page.Categories);
        Assert.Empty(host.Security.ScannedInstances);
    }

    [Fact]
    public async Task ACompleteCleanScanIsGreenWithEveryCategoryShown()
    {
        var host = new Host();
        host.Instances.Instances.Add(Instance("a", "Instance A"));
        host.Instances.Instances.Add(Instance("b", "Instance B"));

        await host.Page.StartScanCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "a", "b" }, host.Security.ScannedInstances);
        Assert.Equal(SecurityOverallState.Clean, host.Page.OverallState);
        Assert.Equal("green", host.Page.StatusKey);
        Assert.True(host.Page.HasScanned);
        Assert.Equal(7, host.Page.Categories.Count);
        Assert.All(host.Page.Categories, category =>
        {
            Assert.Equal("无对应异常", category.CountText);
            Assert.False(category.HasActive);
            Assert.False(category.HasAny);
        });
        Assert.Contains("2/2", host.Page.CoverageText);
        Assert.False(host.Page.ShowSolveButton);
        var start = Assert.Single(host.Operations.Starts);
        Assert.Equal(Ecl.Operations.OperationKind.Verification, start.Kind);
        Assert.True(host.Operations.Handle!.Completed);
    }

    [Fact]
    public async Task FindingsAreGroupedByScanRootAndDriveTheHighestRiskColor()
    {
        var host = new Host();
        host.Instances.Instances.Add(Instance("a", "Instance A"));
        host.Security.Scan = (instanceId, _) => Task.FromResult(Ecl.Core.Result<ThreatScanReport>.Ok(
            FakeSecurityService.Report(
                instanceId,
                FakeSecurityService.Finding("mods/evil.jar", ThreatLevel.HighRisk),
                FakeSecurityService.Finding("config/rule.cfg", ThreatLevel.Suspicious),
                FakeSecurityService.Finding("resourcepacks/pack.zip", ThreatLevel.Notice),
                FakeSecurityService.Finding("launch-arguments", ThreatLevel.Suspicious, sha256: null))));

        await host.Page.StartScanCommand.ExecuteAsync(null);

        Assert.Equal(SecurityOverallState.Critical, host.Page.OverallState);
        Assert.Equal("red", host.Page.StatusKey);
        Assert.True(host.Page.ShowSolveButton);

        var mods = host.Page.Categories.Single(category => category.Key == "mods");
        var config = host.Page.Categories.Single(category => category.Key == "config");
        var resourcePacks = host.Page.Categories.Single(category => category.Key == "resourcepacks");
        var launch = host.Page.Categories.Single(category => category.Key == "launch-arguments");
        Assert.Equal("X", mods.MarkerText);
        Assert.Equal("！", config.MarkerText);
        Assert.Equal("！", resourcePacks.MarkerText);
        Assert.Equal("！", launch.MarkerText);
        Assert.Equal("1 项", mods.CountText);
        Assert.True(mods.Findings[0].CanDelete);
        Assert.False(mods.Findings[0].IsIgnored);
        Assert.Equal("高风险", mods.Findings[0].LevelText);
        Assert.Equal("Instance A", mods.Findings[0].InstanceName);
    }

    [Fact]
    public async Task APartialScanIsNeverGreenAndReportsCoverageFailure()
    {
        var host = new Host();
        host.Instances.Instances.Add(Instance("a", "Instance A"));
        host.Instances.Instances.Add(Instance("b", "Instance B"));
        host.Security.Scan = (instanceId, _) => instanceId == "a"
            ? Task.FromResult(Ecl.Core.Result<ThreatScanReport>.Ok(FakeSecurityService.Report(instanceId)))
            : Task.FromResult(Ecl.Core.Result<ThreatScanReport>.Fail("security.root_missing", "目录不存在"));

        await host.Page.StartScanCommand.ExecuteAsync(null);

        Assert.Equal(SecurityOverallState.Incomplete, host.Page.OverallState);
        Assert.Equal("blue", host.Page.StatusKey);
        Assert.Contains("覆盖问题", host.Page.CoverageText);
        Assert.Contains("目录不存在", host.Page.DetailText);
    }

    [Fact]
    public async Task IgnoringUsesTheExistingRiskAcceptanceRecordAndGraysTheRow()
    {
        var host = new Host();
        host.Instances.Instances.Add(Instance("a", "Instance A"));
        host.Security.Scan = (instanceId, _) => Task.FromResult(Ecl.Core.Result<ThreatScanReport>.Ok(
            FakeSecurityService.Report(
                instanceId,
                FakeSecurityService.Finding("mods/suspicious.jar", ThreatLevel.Suspicious))));
        await host.Page.StartScanCommand.ExecuteAsync(null);
        var row = host.Page.Categories.Single(category => category.Key == "mods").Findings[0];
        host.Dialogs.Results.Enqueue(new DialogResult(DialogOutcome.Primary));

        await host.Page.IgnoreCommand.ExecuteAsync(row);

        var accepted = Assert.Single(host.Security.Accepted);
        Assert.Equal("a", accepted.InstanceId);
        Assert.Same(row.Finding, Assert.Single(accepted.Findings));
        Assert.True(row.IsIgnored);
        Assert.False(row.CanIgnore);
        Assert.Equal("已忽略", row.StateText);
        var mods = host.Page.Categories.Single(category => category.Key == "mods");
        Assert.Equal(0, mods.ActiveCount);
        Assert.Equal("无对应异常", mods.CountText);
        Assert.False(mods.HasActive);
        Assert.Equal(SecurityOverallState.Clean, host.Page.OverallState);
        Assert.Single(host.Dialogs.Requests);
    }

    [Fact]
    public async Task SolvingIssuesFocusesTheFindingsInsteadOfInventingAnotherFlow()
    {
        var host = new Host();
        host.Instances.Instances.Add(Instance("a", "Instance A"));
        host.Security.Scan = (instanceId, _) => Task.FromResult(Ecl.Core.Result<ThreatScanReport>.Ok(
            FakeSecurityService.Report(
                instanceId,
                FakeSecurityService.Finding("config/rule.cfg", ThreatLevel.Suspicious))));
        await host.Page.StartScanCommand.ExecuteAsync(null);
        var focused = 0;
        host.Page.FindingsFocusRequested += (_, _) => focused++;

        host.Page.SolveCommand.Execute(null);

        Assert.Equal(1, focused);
        Assert.True(host.Page.ShowActionHint);
        Assert.Contains("下方分类", host.Page.ActionHintText);
    }

    [Fact]
    public async Task DeletingUsesTheTypedQuarantineOperationAndRemovesTheRow()
    {
        var host = new Host();
        host.Instances.Instances.Add(Instance("a", "Instance A"));
        host.Security.Scan = (instanceId, _) => Task.FromResult(Ecl.Core.Result<ThreatScanReport>.Ok(
            FakeSecurityService.Report(
                instanceId,
                FakeSecurityService.Finding("config/rule.cfg", ThreatLevel.HighRisk))));
        await host.Page.StartScanCommand.ExecuteAsync(null);
        var row = host.Page.Categories.Single(category => category.Key == "config").Findings[0];
        host.Dialogs.Results.Enqueue(new DialogResult(DialogOutcome.Primary));

        await host.Page.DeleteCommand.ExecuteAsync(row);

        var quarantined = Assert.Single(host.Security.Quarantined);
        Assert.Equal("a", quarantined.InstanceId);
        Assert.Equal("config/rule.cfg", quarantined.RelativePath);
        Assert.Empty(host.Page.Categories.Single(category => category.Key == "config").Findings);
        Assert.True(row.IsDeleted);
        Assert.Contains("隔离区", host.Page.StatusText);
    }

    [Fact]
    public async Task LaunchArgumentFindingsHaveNoDeletableFileTarget()
    {
        var host = new Host();
        host.Instances.Instances.Add(Instance("a", "Instance A"));
        host.Security.Scan = (instanceId, _) => Task.FromResult(Ecl.Core.Result<ThreatScanReport>.Ok(
            FakeSecurityService.Report(
                instanceId,
                FakeSecurityService.Finding("launch-arguments", ThreatLevel.Suspicious, sha256: null))));
        await host.Page.StartScanCommand.ExecuteAsync(null);

        var row = host.Page.Categories.Single(category => category.Key == "launch-arguments").Findings[0];

        Assert.False(row.CanDelete);
        Assert.Contains("启动参数", row.DeleteUnavailableReason);
        await host.Page.DeleteCommand.ExecuteAsync(row);
        Assert.Empty(host.Dialogs.Requests);
        Assert.Empty(host.Security.Quarantined);
    }

    [Fact]
    public async Task EnteringThePageDoesNotScanOrLoadHistoricalResults()
    {
        var host = new Host();
        host.Instances.Instances.Add(Instance("a", "Instance A"));

        host.Page.Activate();
        await Task.Yield();

        Assert.Empty(host.Security.ScannedInstances);
        Assert.Equal(SecurityOverallState.NotScanned, host.Page.OverallState);
        // 新的页面对象代表一次新的启动器运行：不会沿用上一次扫描结果。
        var restarted = host.CreatePage();
        Assert.Equal(SecurityOverallState.NotScanned, restarted.OverallState);
        Assert.Empty(restarted.Categories);
    }

    [Fact]
    public void TheViewHidesPathsAndUsesTheSharedStatusCircle()
    {
        var xaml = ReadSource("src/ECL.App/Views/SecurityCenterView.xaml");

        Assert.Contains("SecurityStatusCircle", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("RelativePath", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<TabControl", xaml, StringComparison.Ordinal);
        Assert.Contains("security.finding.ignore", xaml, StringComparison.Ordinal);
        Assert.Contains("security.finding.delete", xaml, StringComparison.Ordinal);
        Assert.Contains("VirtualizingPanel.IsVirtualizing=\"True\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void TheViewModelDoesNotReadPersistedScans()
    {
        var source = ReadSource("src/ECL.App/ViewModels/Security/SecurityCenterViewModel.cs");

        Assert.DoesNotContain("GetLatestScanAsync", source, StringComparison.Ordinal);
        Assert.Contains("AcceptRiskAsync", source, StringComparison.Ordinal);
        Assert.Contains("QuarantineAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSecurityViewsLoadUnderTheSharedTheme()
    {
        using var ui = new UiThread();
        ui.Invoke(() =>
        {
            TestApplication.Ensure();
            _ = new Ecl.App.Controls.SecurityStatusCircle();
            _ = new Ecl.App.Views.SecurityView();
            _ = new Ecl.App.Views.SecurityCenterView();
            _ = new Ecl.App.Views.RuntimeStatusView();
        });
    }

    private static GameInstance Instance(string id, string name) => new()
    {
        Id = id,
        Name = name,
        GameVersion = "1.20.1",
        VersionId = "1.20.1-vanilla",
    };

    private static string ReadSource(string relativePath)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "ECL.sln")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"找不到源文件：{path}");
        return File.ReadAllText(path);
    }

    private sealed class Host
    {
        public Host()
        {
            Instances = new FakeInstanceService();
            Security = new FakeSecurityService();
            Dialogs = new FakeDialogEngine();
            Operations = new RecordingOperationService();
            Page = CreatePage();
        }

        public FakeInstanceService Instances { get; }

        public FakeSecurityService Security { get; }

        public FakeDialogEngine Dialogs { get; }

        public RecordingOperationService Operations { get; }

        public SecurityCenterViewModel Page { get; }

        public SecurityCenterViewModel CreatePage() => new(
            Instances,
            Security,
            Dialogs,
            Operations,
            new JsonLocalizer(CultureInfo.GetCultureInfo("zh-CN")));
    }
}
