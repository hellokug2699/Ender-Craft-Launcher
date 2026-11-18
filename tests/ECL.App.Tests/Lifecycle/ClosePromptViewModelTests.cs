// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.App.Dialogs;
using Ecl.App.Shell.Lifecycle;
using Ecl.App.Tests.Support;
using Xunit;

namespace Ecl.App.Tests.Lifecycle;

/// <summary>
/// F-12 关闭询问内容：三个按钮提交稳定语义、取消默认不提交、“记住我的选择”随决策返回，
/// 以及托盘不可用时只禁用最小化而不是让整个询问失效。
/// </summary>
public sealed class ClosePromptViewModelTests
{
    [Fact]
    public void MinimizeReturnsPrimaryWithRememberFlag()
    {
        var (viewModel, session) = Create(canMinimizeToTray: true);
        viewModel.RememberChoice = true;

        viewModel.MinimizeCommand.Execute(null);

        Assert.Equal(DialogOutcome.Primary, session.Result!.Outcome);
        var decision = Assert.IsType<ClosePromptDecision>(session.Result.Value);
        Assert.Equal(CloseChoice.MinimizeToTray, decision.Choice);
        Assert.True(decision.Remember);
    }

    [Fact]
    public void ExitReturnsSecondaryAndCancelReturnsCancel()
    {
        var (viewModel, session) = Create(canMinimizeToTray: true);

        viewModel.ExitCommand.Execute(null);
        Assert.Equal(DialogOutcome.Secondary, session.Result!.Outcome);
        Assert.Equal(
            CloseChoice.Exit,
            Assert.IsType<ClosePromptDecision>(session.Result.Value).Choice);

        var (second, secondSession) = Create(canMinimizeToTray: true);
        second.CancelCommand.Execute(null);
        Assert.Equal(DialogOutcome.Cancel, secondSession.Result!.Outcome);
    }

    [Fact]
    public void MinimizeIsIgnoredWhenTrayIsUnavailableButExitStillWorks()
    {
        var (viewModel, session) = Create(canMinimizeToTray: false);

        Assert.False(viewModel.CanMinimizeToTray);
        Assert.True(viewModel.HasMinimizeUnavailableReason);

        viewModel.MinimizeCommand.Execute(null);
        Assert.True(session.IsOpen);

        viewModel.ExitCommand.Execute(null);
        Assert.Equal(DialogOutcome.Secondary, session.Result!.Outcome);
    }

    private static (ClosePromptViewModel ViewModel, FakeDialogSession Session) Create(bool canMinimizeToTray)
    {
        var viewModel = new ClosePromptViewModel(
            new ClosePromptRequest(
                "关闭 ECL 前要做什么？",
                canMinimizeToTray,
                "系统托盘不可用，无法最小化到托盘。"),
            minimizeText: "最小化到托盘",
            exitText: "退出 ECL",
            cancelText: "取消",
            rememberText: "记住我的选择",
            unavailableText: "系统托盘不可用，无法最小化到托盘。");

        var session = new FakeDialogSession();
        ((IDialogSessionAware)viewModel).Attach(session);
        return (viewModel, session);
    }
}
