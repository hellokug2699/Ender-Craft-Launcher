// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.IO;
using Ecl.App.Tests.Support;
using Ecl.App.ViewModels;
using Ecl.Core;
using Ecl.Core.Localization;
using Ecl.Core.Storage;
using Xunit;

namespace Ecl.App.Tests.Dialogs;

/// <summary>
/// B10 首次启动向导（ADR 0052、ADR 0018）：业务状态从窗口 code-behind 迁入页面对象；
/// 推荐位置、路径校验、采用旧版与“稍后配置”都只调用 <see cref="IWorkspaceService"/>。
/// </summary>
public sealed class WorkspaceSetupPageTests
{
    [Fact]
    public void InitializationReadsSuggestionsLegacyDataAndPrimaryPath()
    {
        var host = new Host();
        host.Workspace.Primary = Root("/srv/primary");
        host.Workspace.Suggested.Add(new WorkspaceSuggestion("/srv/pick", "workspace.reason.fixedDrive", true));
        host.Workspace.Legacy = new WorkspaceLegacyInfo(true, "/srv/legacy", 3, 7);

        host.Page.Initialize();

        var suggestion = Assert.Single(host.Page.Suggestions);
        Assert.Equal("/srv/pick", suggestion.Path);
        Assert.Equal("非系统固定磁盘", suggestion.Reason);
        Assert.True(host.Page.HasLegacy);
        Assert.Contains("/srv/legacy", host.Page.LegacySummary);
        Assert.Contains("3", host.Page.LegacySummary);
        Assert.Equal("/srv/primary", host.Page.PathText);
        // 已登记主工作区不在推荐列表中时，不把推荐项误显示为当前选择。
        Assert.Null(host.Page.SelectedSuggestion);
        Assert.True(host.Page.CanAccept);
    }

    [Fact]
    public void WithoutAPrimaryWorkspaceTheFirstSuggestionBecomesTheDefault()
    {
        var host = new Host();
        host.Workspace.Suggested.Add(new WorkspaceSuggestion("/srv/pick", "workspace.reason.fixedDrive", true));

        host.Page.Initialize();

        Assert.Equal("/srv/pick", host.Page.PathText);
        Assert.Same(host.Page.Suggestions[0], host.Page.SelectedSuggestion);
    }

    [Fact]
    public async Task ValidationReportsReadyWarningAndFreeSpaceFromTheService()
    {
        var host = new Host();
        host.Workspace.Suggested.Add(new WorkspaceSuggestion("/srv/pick", "workspace.reason.fixedDrive", true));
        host.Page.Initialize();

        await host.Page.ValidateAsync();

        Assert.Contains("目录可用", host.Page.StatusText);
        Assert.Contains("位于系统盘", host.Page.StatusText);
        Assert.Contains("GB", host.Page.StatusText);
        Assert.Equal(1, host.Workspace.CheckCalls);
    }

    [Fact]
    public async Task ValidationDistinguishesMissingAndInvalidLocations()
    {
        var host = new Host();
        host.Page.PathText = "/srv/missing";
        host.Workspace.CheckResult = new WorkspacePathCheck(
            "/srv/missing",
            true,
            WorkspaceStatus.Missing,
            null,
            null,
            null,
            []);

        await host.Page.ValidateAsync();
        Assert.Contains("尚不存在", host.Page.StatusText);

        host.Workspace.CheckResult = WorkspacePathCheck.Invalid(
            "/srv/invalid",
            "workspace.invalid",
            "路径不可用");
        await host.Page.ValidateAsync();
        Assert.Contains("路径不可用", host.Page.StatusText);
    }

    [Fact]
    public async Task AcceptingTheLocationSignalsCompletionOnlyAfterTheServiceSucceeds()
    {
        var host = new Host();
        host.Page.PathText = "/srv/new";
        var completed = new List<bool>();
        host.Page.Completed += (_, accepted) => completed.Add(accepted);

        await host.Page.AcceptCommand.ExecuteAsync(null);

        Assert.True(Assert.Single(completed));
        Assert.Equal(1, host.Workspace.InitializeCalls);
        Assert.False(host.Page.IsBusy);
    }

    [Fact]
    public async Task FailingInitializationKeepsTheWizardOpenWithTheRealReason()
    {
        var host = new Host();
        host.Page.PathText = "/srv/new";
        host.Workspace.InitializeResult = Result<WorkspaceRoot>.Fail("workspace.not_writable", "目录不可写。");
        var completed = 0;
        host.Page.Completed += (_, _) => completed++;

        await host.Page.AcceptCommand.ExecuteAsync(null);

        Assert.Equal(0, completed);
        Assert.Contains("目录不可写", host.Page.StatusText);
        Assert.False(host.Page.IsBusy);
    }

    [Fact]
    public async Task EmptyPathCannotBeAccepted()
    {
        var host = new Host();
        host.Page.PathText = "   ";

        Assert.False(host.Page.CanAccept);
        await host.Page.AcceptCommand.ExecuteAsync(null);

        Assert.Equal(0, host.Workspace.InitializeCalls);
    }

    [Fact]
    public async Task AdoptingLegacyDataUsesTheWorkspaceServiceAndCloses()
    {
        var host = new Host();
        host.Workspace.Legacy = new WorkspaceLegacyInfo(true, "/srv/legacy", 1, 2);
        host.Page.Initialize();
        bool? result = null;
        host.Page.Completed += (_, accepted) => result = accepted;

        await host.Page.AdoptLegacyCommand.ExecuteAsync(null);

        Assert.True(result);
        Assert.Equal(1, host.Workspace.AdoptCalls);
    }

    [Fact]
    public async Task ConfigureLaterUsesTheOnboardingSkipContract()
    {
        var host = new Host();
        bool? result = null;
        host.Page.Completed += (_, accepted) => result = accepted;

        await host.Page.SkipCommand.ExecuteAsync(null);

        Assert.False(result);
        Assert.Equal(1, host.Workspace.SkipCalls);
    }

    [Fact]
    public void TheWindowCodeBehindContainsNoWorkspaceBusinessCalls()
    {
        var codeBehind = ReadSource("src/ECL.App/Views/WorkspaceSetupWindow.xaml.cs");

        Assert.DoesNotContain("InitializeAsync", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("AdoptLegacyAsync", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("SkipOnboardingAsync", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("IWorkspaceService", codeBehind, StringComparison.Ordinal);
        Assert.Contains("WorkspaceSetupViewModel", codeBehind, StringComparison.Ordinal);
    }

    private static WorkspaceRoot Root(string path) =>
        new("primary", "Primary", path, WorkspaceKind.Managed, WorkspaceStatus.Ready);

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
            Workspace = new FakeWorkspaceService();
            Page = new WorkspaceSetupViewModel(
                Workspace,
                new JsonLocalizer(CultureInfo.GetCultureInfo("zh-CN")));
        }

        public FakeWorkspaceService Workspace { get; }

        public WorkspaceSetupViewModel Page { get; }
    }
}
