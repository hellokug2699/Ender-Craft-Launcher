// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Core.Runtime;

/// <summary>
/// 应用运行时生命周期。单向推进：Created → Starting → Running → Stopping → Disposing → Stopped。
/// 启动失败进入 Faulted，并在有界时间内走完清理路径；状态模型只表达协调契约，
/// 不要求业务模块复制同一组字段（ADR 0039 §1）。
/// </summary>
public enum ApplicationLifecycleState
{
    Created,
    Starting,
    Running,
    Stopping,
    Disposing,
    Stopped,
    Faulted,
}

/// <summary>后台工作类别，用于诊断与关闭报告分组；不改变各模块自身的资源所有权。</summary>
public enum BackgroundWorkKind
{
    Monitor,
    Queue,
    Periodic,
    Io,
    Other,
}

/// <summary>
/// 一项需要跨调用栈存活的后台工作。短生命周期、由调用方 await 的普通异步调用不需要登记；
/// Fire-and-forget 不是默认模式（ADR 0039 §3）。
/// </summary>
public sealed record BackgroundWorkDescriptor(
    string Id,
    string Owner,
    BackgroundWorkKind Kind = BackgroundWorkKind.Other,
    TimeSpan? ShutdownTimeout = null);

public enum BackgroundWorkOutcomeState
{
    Completed,
    Canceled,
    Faulted,
    TimedOut,
    Rejected,
}

public sealed record BackgroundWorkOutcome(
    string Id,
    string Owner,
    BackgroundWorkKind Kind,
    BackgroundWorkOutcomeState State,
    TimeSpan Duration,
    string? Error);

public enum ShutdownStepState
{
    Completed,
    Faulted,
    TimedOut,
}

public sealed record ShutdownStepOutcome(string Name, ShutdownStepState State, TimeSpan Duration, string? Error);

public enum ResourceDisposalState
{
    Disposed,
    Faulted,
    TimedOut,
}

public sealed record ResourceDisposalOutcome(string Owner, ResourceDisposalState State, TimeSpan Duration, string? Error);

/// <summary>
/// 关闭结果。超时、清理失败与启动异常都保留在报告中，避免“进程已退出”被误当成“已优雅关闭”。
/// </summary>
public sealed record ShutdownReport
{
    public required IReadOnlyList<BackgroundWorkOutcome> Work { get; init; }

    public required IReadOnlyList<ShutdownStepOutcome> Steps { get; init; }

    public required IReadOnlyList<ResourceDisposalOutcome> Resources { get; init; }

    public required TimeSpan Duration { get; init; }

    /// <summary>启动失败时保留原始异常；清理失败不得掩盖它（ADR 0039 §1）。</summary>
    public Exception? StartupError { get; init; }

    public bool TimedOut =>
        Work.Any(item => item.State == BackgroundWorkOutcomeState.TimedOut) ||
        Steps.Any(item => item.State == ShutdownStepState.TimedOut) ||
        Resources.Any(item => item.State == ResourceDisposalState.TimedOut);

    public bool HasFailures =>
        StartupError is not null ||
        Work.Any(item => item.State is BackgroundWorkOutcomeState.Faulted or BackgroundWorkOutcomeState.TimedOut) ||
        Steps.Any(item => item.State is ShutdownStepState.Faulted or ShutdownStepState.TimedOut) ||
        Resources.Any(item => item.State is ResourceDisposalState.Faulted or ResourceDisposalState.TimedOut);

    public bool IsClean => !HasFailures;

    /// <summary>可直接写入日志的失败摘要；为空表示关闭过程中没有记录的失败或超时。</summary>
    public IReadOnlyList<string> Issues
    {
        get
        {
            var issues = new List<string>();
            if (StartupError is not null)
            {
                issues.Add($"启动失败：{StartupError.Message}");
            }

            foreach (var item in Work)
            {
                if (item.State is BackgroundWorkOutcomeState.Faulted or BackgroundWorkOutcomeState.TimedOut)
                {
                    issues.Add($"后台工作 {item.Id}（{item.Owner}）：{item.State}{Format(item.Error)}");
                }
            }

            foreach (var step in Steps)
            {
                if (step.State is ShutdownStepState.Faulted or ShutdownStepState.TimedOut)
                {
                    issues.Add($"关闭步骤 {step.Name}：{step.State}{Format(step.Error)}");
                }
            }

            foreach (var resource in Resources)
            {
                if (resource.State is ResourceDisposalState.Faulted or ResourceDisposalState.TimedOut)
                {
                    issues.Add($"资源释放 {resource.Owner}：{resource.State}{Format(resource.Error)}");
                }
            }

            return issues;
        }
    }

    private static string Format(string? error) => string.IsNullOrWhiteSpace(error) ? "。" : $"（{error}）";
}

public sealed class ApplicationLifecycleOptions
{
    /// <summary>单项后台工作或关闭步骤的默认等待上限。</summary>
    public TimeSpan DefaultShutdownTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>整个关闭流程的总预算；超过后剩余工作按超时记录，不再无限等待。</summary>
    public TimeSpan TotalShutdownTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>应用级生命周期协调入口（ADR 0039 §2）。模块仍然负责停止并释放自己拥有的资源。</summary>
public interface IApplicationLifecycle
{
    ApplicationLifecycleState State { get; }

    event EventHandler<ApplicationLifecycleState>? StateChanged;

    /// <summary>进入 Starting：组合根正在建立服务并执行初始化。</summary>
    void BeginStart();

    /// <summary>初始化完成，开始接收用户操作与应用级后台工作。</summary>
    void MarkRunning();

    /// <summary>
    /// 登记并启动长期后台工作。返回句柄可用于观察完成状态；异常由协调器观察并记入关闭报告。
    /// 在 Stopping/Disposing/Stopped 状态下登记会被拒绝（句柄状态 Rejected）。
    /// </summary>
    BackgroundWorkHandle TrackWork(BackgroundWorkDescriptor descriptor, Func<CancellationToken, Task> work);

    /// <summary>登记一个已经启动、原本可能 fire-and-forget 的任务，并观察其异常。</summary>
    BackgroundWorkHandle TrackTask(BackgroundWorkDescriptor descriptor, Task task);

    /// <summary>
    /// 登记模块级异步停止步骤。按登记顺序的逆序执行：依赖其他模块的模块通常后登记，
    /// 因而先停止（ADR 0039 §5）。步骤必须幂等并自行处理取消。
    /// </summary>
    void RegisterStopStep(string name, Func<CancellationToken, Task> step, TimeSpan? timeout = null);

    /// <summary>登记由应用持有的异步资源；关闭时在停止步骤之后按登记逆序释放。</summary>
    void RegisterResource(string owner, Func<ValueTask> disposeAsync, TimeSpan? timeout = null);

    /// <summary>
    /// 执行关闭：停止接收新工作 → 取消并等待受管理工作 → 执行停止步骤 → 释放资源 → 记录报告。
    /// 幂等；重复调用返回同一个结果。
    /// </summary>
    Task<ShutdownReport> ShutdownAsync(CancellationToken cancellationToken = default);

    /// <summary>启动失败时的受控清理；保留原始异常并返回关闭报告。</summary>
    Task<ShutdownReport> FailStartupAsync(Exception exception, CancellationToken cancellationToken = default);
}

/// <summary>后台工作句柄：只读观察状态，不转移资源所有权。</summary>
public sealed class BackgroundWorkHandle
{
    internal BackgroundWorkHandle(string id, Task completion, CancellationToken token, bool rejected)
    {
        Id = id;
        Completion = completion;
        Token = token;
        IsRejected = rejected;
    }

    public string Id { get; }

    /// <summary>工作完成（含失败/取消）时完成；被拒绝的登记立即完成。</summary>
    public Task Completion { get; }

    public CancellationToken Token { get; }

    public bool IsRejected { get; }
}
