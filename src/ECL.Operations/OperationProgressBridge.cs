// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Operations;

/// <summary>
/// 业务服务进度与任务中心之间的桥接：
/// <list type="bullet">
/// <item>登记一个 Operation，并把业务进度投影成 <see cref="OperationProgressUpdate"/>；</item>
/// <item>进度同时继续转发给原有的 <see cref="IProgress{T}"/> 调用方，旧 API 不受影响；</item>
/// <item>在作用域内抑制子服务的重复任务（见 <see cref="OperationScope"/>）；</item>
/// <item>未注册 <see cref="IOperationService"/> 时退化为普通进度转发。</item>
/// </list>
/// </summary>
/// <typeparam name="T">业务服务自己的进度类型。</typeparam>
public sealed class OperationProgressBridge<T> : IProgress<T>, IDisposable
{
    private readonly IProgress<T>? _inner;
    private readonly Func<T, OperationProgressUpdate> _project;
    private readonly IOperationService? _operations;
    private readonly IDisposable? _scope;
    private int _disposed;

    public OperationProgressBridge(
        IOperationService? operations,
        OperationStart start,
        IProgress<T>? inner,
        Func<T, OperationProgressUpdate> project)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(project);

        _inner = inner;
        _project = project;

        // 已有上层任务时只转发进度：一次业务动作最多产生一个任务中心条目。
        if (operations is null || OperationScope.IsActive)
        {
            return;
        }

        _operations = operations;
        Handle = operations.Begin(start);
        _scope = OperationScope.Enter(Handle);
    }

    /// <summary>任务写入句柄；未接入任务中心或处于上层任务作用域时为 null。</summary>
    public IOperationHandle? Handle { get; }

    public Guid? OperationId => Handle?.Id;

    /// <summary>当前桥接是否拥有自己的任务（用于判断嵌套与未接入两种退化情况）。</summary>
    public bool IsOwner => Handle is not null;

    /// <summary>把任务中心暴露给业务服务，同时保持进度转发不变。</summary>
    public void Report(T value)
    {
        _inner?.Report(value);
        if (Handle is not null)
        {
            Handle.Report(_project(value));
        }
    }

    /// <summary>为任务绑定取消与重试能力；未拥有任务时返回 false。</summary>
    public bool AttachControl(CancellationTokenSource cancellation, Func<CancellationToken, Task>? retry = null)
    {
        ArgumentNullException.ThrowIfNull(cancellation);

        if (_operations is null || Handle is null)
        {
            return false;
        }

        return _operations.AttachControl(Handle.Id, new OperationControl(cancellation, retry));
    }

    public void Complete(OperationProgressUpdate? finalUpdate = null) => Handle?.Complete(finalUpdate);

    public void Fail(OperationError error) => Handle?.Fail(error);

    public void Fail(Exception exception, string code) => Handle?.Fail(OperationErrors.FromException(code, exception));

    public void Cancel() => Handle?.Cancel();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // 先离开作用域，再释放句柄：未完成的任务会被标记为取消。
        _scope?.Dispose();
        Handle?.Dispose();
    }
}
