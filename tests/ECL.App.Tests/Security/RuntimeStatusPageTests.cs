// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.IO;
using Ecl.App.Services;
using Ecl.App.Tests.Support;
using Ecl.App.ViewModels.Security;
using Ecl.Core.Localization;
using Xunit;

namespace Ecl.App.Tests.Security;

/// <summary>
/// 状态中心（hints/ui/pages/runtime-status.md）：组件健康与运行状态独立表达；
/// 不可用时不伪造组件状态；修复交接未实现时按钮存在但不可用，页面不会自行修复或关闭 ECL。
/// </summary>
public sealed class RuntimeStatusPageTests
{
    [Fact]
    public async Task UnavailableInspectionShowsTheRealReasonInsteadOfComponents()
    {
        var host = new Host();
        host.Service.Reports.Enqueue(RuntimeStatusReport.Unavailable("未找到当前版本指针。"));

        await host.Page.ActivateAsync();

        Assert.False(host.Page.IsAvailable);
        Assert.Equal("无法获取组件状态", host.Page.StatusText);
        Assert.Equal("unknown", host.Page.StatusKey);
        Assert.Equal("—", host.Page.Glyph);
        Assert.Contains("未找到当前版本指针", host.Page.DetailText);
        Assert.Empty(host.Page.Components);
        Assert.False(host.Page.ShowRepair);
        Assert.False(host.Page.ShowEmptyHint);
    }

    [Fact]
    public async Task HealthyComponentsShowAGreenCheckmarkWithoutTheRepairButton()
    {
        var host = new Host();
        host.Service.Reports.Enqueue(Report(
            ("ECL.exe", RuntimeHealth.Healthy, RuntimeRunState.Running, null),
            ("Ecl.dll", RuntimeHealth.Healthy, RuntimeRunState.Running, null),
            ("config.json", RuntimeHealth.Healthy, RuntimeRunState.Stopped, null)));

        await host.Page.ActivateAsync();

        Assert.True(host.Page.IsAvailable);
        Assert.Equal("green", host.Page.StatusKey);
        Assert.Equal("✓", host.Page.Glyph);
        Assert.Equal("ECL 正在按规划运行。", host.Page.StatusText);
        Assert.Equal(3, host.Page.Components.Count);
        Assert.False(host.Page.ShowRepair);
        Assert.False(string.IsNullOrWhiteSpace(host.Page.RepairUnavailableText));
    }

    [Fact]
    public async Task AnyDamagedComponentTurnsTheCircleRedAndShowsTheReason()
    {
        var host = new Host();
        host.Service.Reports.Enqueue(Report(
            ("ECL.exe", RuntimeHealth.Healthy, RuntimeRunState.Running, null),
            ("Ecl.dll", RuntimeHealth.Damaged, RuntimeRunState.Stopped, "文件大小与安装清单不符。"),
            ("ecl.test", RuntimeHealth.Warning, RuntimeRunState.Stopped, "扩展文件缺失：entry.dll")));

        await host.Page.ActivateAsync();

        Assert.Equal("red", host.Page.StatusKey);
        Assert.Equal("X", host.Page.Glyph);
        Assert.True(host.Page.ShowRepair);
        Assert.Equal(3, host.Page.Components.Count);
        Assert.Equal("已停止", host.Page.Components[1].RunStateText);
        Assert.Equal("damaged", host.Page.Components[1].HealthKey);
        Assert.Equal("文件大小与安装清单不符。", host.Page.Components[1].Reason);
    }

    [Fact]
    public async Task WarningComponentsTurnTheCircleYellow()
    {
        var host = new Host();
        host.Service.Reports.Enqueue(Report(
            ("配置", RuntimeHealth.Healthy, RuntimeRunState.Stopped, null),
            ("安装清单来源", RuntimeHealth.Warning, RuntimeRunState.Stopped, "缺少更新公钥。")));

        await host.Page.ActivateAsync();

        Assert.Equal("yellow", host.Page.StatusKey);
        Assert.Equal("?", host.Page.Glyph);
        Assert.True(host.Page.ShowRepair);
        Assert.True(host.Page.Components[1].HasReason);
    }

    [Fact]
    public async Task UnknownHealthUsesNeutralColorAndTheUnknownReasonText()
    {
        var host = new Host();
        host.Service.Reports.Enqueue(Report(
            ("unknown", RuntimeHealth.Unknown, RuntimeRunState.Unknown, null)));

        await host.Page.ActivateAsync();

        Assert.Equal("unknown", host.Page.StatusKey);
        Assert.Equal("—", host.Page.Glyph);
        Assert.Equal("未知", host.Page.Components[0].RunStateText);
        Assert.Equal("未知原因", host.Page.Components[0].Reason);
    }

    [Fact]
    public async Task HealthAndRunStateAreIndependent()
    {
        var host = new Host();
        host.Service.Reports.Enqueue(Report(
            ("healthy-stopped", RuntimeHealth.Healthy, RuntimeRunState.Stopped, null)));

        await host.Page.ActivateAsync();

        Assert.Equal("healthy", host.Page.Components[0].HealthKey);
        Assert.Equal("已停止", host.Page.Components[0].RunStateText);
        Assert.False(host.Page.Components[0].HasReason);
    }

    [Fact]
    public async Task ThePageInspectsOnlyOncePerActivation()
    {
        var host = new Host();
        host.Service.Reports.Enqueue(Report(("ECL.exe", RuntimeHealth.Healthy, RuntimeRunState.Running, null)));

        await host.Page.ActivateAsync();
        await host.Page.ActivateAsync();

        Assert.Equal(1, host.Service.Calls);
    }

    [Fact]
    public void TheSecurityAreaHasTwoHorizontalPagesAndKeepsState()
    {
        var host = new Host();
        var area = new SecurityViewModel(host.Center, host.Page, Localizer());

        Assert.Equal(2, area.Pages.Count);
        Assert.Equal("security.center", area.Pages[0].Key);
        Assert.Equal("security.runtime-status", area.Pages[1].Key);
        Assert.Same(area.SecurityCenter, area.CurrentPage);

        area.SelectedPage = area.Pages[1];

        Assert.Same(area.RuntimeStatus, area.CurrentPage);
        Assert.Equal(1, host.Service.Calls);
    }

    [Fact]
    public void TheRuntimeViewUsesTheSharedCircleAndHasNoRowActions()
    {
        var xaml = ReadSource("src/ECL.App/Views/RuntimeStatusView.xaml");

        Assert.Contains("SecurityStatusCircle", xaml, StringComparison.Ordinal);
        Assert.Contains("PrimaryEnabled=\"False\"", xaml, StringComparison.Ordinal);
        Assert.Contains("RepairUnavailableText", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("RepairCommand", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("IgnoreCommand", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("DeleteCommand", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSecurityNavigationIsRegisteredBeforeAboutAndTheViewsAreMapped()
    {
        var shell = ReadSource("src/ECL.App/Shell/ShellViewModel.cs");
        var securityIndex = shell.IndexOf("nav.security", StringComparison.Ordinal);
        var aboutIndex = shell.IndexOf("nav.about", StringComparison.Ordinal);
        Assert.True(securityIndex >= 0 && aboutIndex > securityIndex);
        Assert.Contains("\"security\", \"security\"", shell, StringComparison.Ordinal);

        var app = ReadSource("src/ECL.App/App.xaml");
        Assert.Contains("SecurityViewModel", app, StringComparison.Ordinal);
        Assert.Contains("SecurityCenterViewModel", app, StringComparison.Ordinal);
        Assert.Contains("RuntimeStatusViewModel", app, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheRuntimeServiceRefusesToInventStateWithoutAnInstallLayout()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ecl-runtime-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var service = new RuntimeStatusService(
                new FakeAdditionsService(),
                Localizer(),
                environment: new RuntimeStatusEnvironment(root));

            var report = await service.InspectAsync();

            Assert.False(report.IsAvailable);
            Assert.False(string.IsNullOrWhiteSpace(report.UnavailableReason));
            Assert.Empty(report.Components);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TheGlobalBackgroundTaskEntryIsNamedProgressNotRuntimeStatus()
    {
        var zh = ReadSource("src/ECL.Core/Localization/Locales/zh-CN.json");
        var en = ReadSource("src/ECL.Core/Localization/Locales/en-US.json");

        Assert.Contains("\"output.tab.tasks\": \"进度\"", zh, StringComparison.Ordinal);
        Assert.Contains("\"output.tab.tasks\": \"Progress\"", en, StringComparison.Ordinal);
        Assert.DoesNotContain("\"output.tab.tasks\": \"状态中心\"", zh, StringComparison.Ordinal);
    }

    private static RuntimeStatusReport Report(params (string Name, RuntimeHealth Health, RuntimeRunState RunState, string? Reason)[] rows) =>
        new(
            true,
            null,
            "0.8.0",
            DateTimeOffset.Now,
            rows.Select(row => new RuntimeComponentStatus(row.Name, row.Health, row.RunState, row.Reason)).ToArray())
        {
            IsComplete = true,
            CoverageText = "受监控安装文件 3 个 · 扩展 1 个 · 已核对 SHA-256",
        };

    private static ILocalizer Localizer() => new JsonLocalizer(CultureInfo.GetCultureInfo("zh-CN"));

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
            Service = new FakeRuntimeStatusService();
            Page = new RuntimeStatusViewModel(Service, Localizer());
            Center = new SecurityCenterViewModel(
                new FakeInstanceService(),
                new FakeSecurityService(),
                new FakeDialogEngine(),
                new RecordingOperationService(),
                Localizer());
        }

        public FakeRuntimeStatusService Service { get; }

        public RuntimeStatusViewModel Page { get; }

        public SecurityCenterViewModel Center { get; }
    }
}
