// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Operations;

/// <summary>
/// 当前异步上下文中正在执行的业务任务。
/// <para>
/// 业务服务登记任务后进入作用域；作用域内的“子服务”（下载批次、整合包导入、
/// 内容安装等）会复用上层任务，而不是再登记一个重复任务。这样一次安装只会出现
/// 一个任务中心条目，进度仍通过各层已有的 <see cref="IProgress{T}"/> 转发。
/// </para>
/// <para>
/// 子服务只能借用上层任务上报瞬时状态（例如 <c>Retrying</c>），
/// 任务的生命周期仍由拥有它的业务服务决定。
/// </para>
/// </summary>
public static class OperationScope
{
    private static readonly AsyncLocal<IOperationHandle?> CurrentHandle = new();

    /// <summary>当前作用域中的任务句柄；不在任务作用域内时为 <c>null</c>。</summary>
    public static IOperationHandle? Current => CurrentHandle.Value;

    /// <summary>当前作用域中的任务 Id；不在任务作用域内时为 <c>null</c>。</summary>
    public static Guid? CurrentId => CurrentHandle.Value?.Id;

    /// <summary>当前是否已有一个上层任务在承载进度。</summary>
    public static bool IsActive => CurrentHandle.Value is not null;

    internal static IDisposable Enter(IOperationHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        var previous = CurrentHandle.Value;
        CurrentHandle.Value = handle;
        return new Scope(previous);
    }

    private sealed class Scope(IOperationHandle? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CurrentHandle.Value = previous;
        }
    }
}
