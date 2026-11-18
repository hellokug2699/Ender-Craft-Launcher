// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.App.Services.Lifecycle;
using Ecl.App.Shell.Lifecycle;

namespace Ecl.App.Tests.Support;

/// <summary>
/// 托盘替身：只记录显示/隐藏与事件，不创建任何 WinForms 对象，
/// 因此窗口状态机测试可以在无托盘的真实环境中确定性地覆盖“可用/不可用”两条路径。
/// </summary>
internal sealed class FakeTrayHost : ITrayHost, ITrayHostStatus
{
    public bool IsAvailable { get; set; } = true;

    public string? UnavailableReasonKey { get; set; }

    public string? UnavailableDetail { get; set; }

    public int ShowCalls { get; private set; }

    public int HideCalls { get; private set; }

    public int DisposeCalls { get; private set; }

    public event EventHandler? RestoreRequested;

    public event EventHandler? ExitRequested;

    public void Show()
    {
        ShowCalls++;
        if (IsAvailable)
        {
            IsShown = true;
        }
    }

    public void Hide()
    {
        HideCalls++;
        IsShown = false;
    }

    public bool IsShown { get; private set; }

    public void Dispose() => DisposeCalls++;

    public void RaiseRestoreRequested() => RestoreRequested?.Invoke(this, EventArgs.Empty);

    public void RaiseExitRequested() => ExitRequested?.Invoke(this, EventArgs.Empty);
}

/// <summary>可编程的关闭询问：记录调用并把所有分支变成同步结果。</summary>
internal sealed class FakeClosePrompt : IClosePrompt
{
    public CloseChoice NextChoice { get; set; } = CloseChoice.Cancel;

    public bool RememberOnAsk { get; set; }

    public bool ConfirmExitResult { get; set; }

    public bool BlockAsk { get; set; }

    public TaskCompletionSource<bool>? AskGate { get; private set; }

    public int AskCalls { get; private set; }

    public int ConfirmCalls { get; private set; }

    public CloseReadiness? LastReadiness { get; private set; }

    public ClosePromptRequest? LastRequest { get; private set; }

    public async Task<ClosePromptDecision> AskAsync(
        ClosePromptRequest request,
        CancellationToken cancellationToken = default)
    {
        AskCalls++;
        LastRequest = request;
        if (BlockAsk)
        {
            AskGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await AskGate.Task.ConfigureAwait(false);
        }

        return new ClosePromptDecision(NextChoice, RememberOnAsk);
    }

    public Task<bool> ConfirmExitAsync(CloseReadiness readiness, CancellationToken cancellationToken = default)
    {
        ConfirmCalls++;
        LastReadiness = readiness;
        return Task.FromResult(ConfirmExitResult);
    }

    public void ReleaseAsk() => AskGate?.TrySetResult(true);
}

/// <summary>固定返回指定状态的就绪探测。</summary>
internal sealed class FakeCloseReadinessProbe : ICloseReadinessProbe
{
    public CloseReadiness Readiness { get; set; } = new(0, 0, false, false);

    public int Calls { get; private set; }

    public CloseReadiness Read()
    {
        Calls++;
        return Readiness;
    }
}

/// <summary>只记录调用的关闭协调器；避免测试触碰真实生命周期与容器。</summary>
internal sealed class FakeShutdownCoordinator : IAppShutdownCoordinator
{
    public int ShutdownCalls { get; private set; }

    public bool IsShuttingDown => ShutdownCalls > 0;

    public Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        ShutdownCalls++;
        return Task.CompletedTask;
    }
}
