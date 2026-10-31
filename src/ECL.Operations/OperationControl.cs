// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Operations;

/// <summary>
/// 由业务服务实现的取消/重试控制：取消触发任务自己的取消源，
/// 重试通过工厂重新执行同一业务动作（通常会产生新的 Operation 快照）。
/// </summary>
public sealed class OperationControl(
    CancellationTokenSource cancellation,
    Func<CancellationToken, Task>? retry = null) : IOperationControl
{
    private readonly CancellationTokenSource _cancellation =
        cancellation ?? throw new ArgumentNullException(nameof(cancellation));

    public Func<CancellationToken, Task>? Retry { get; } = retry;

    public bool TryCancel()
    {
        try
        {
            _cancellation.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }
}
