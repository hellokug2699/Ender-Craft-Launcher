// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows.Threading;

namespace Ecl.App.Tests;

/// <summary>
/// 测试用 STA UI 线程：持有独立 Dispatcher，供弹窗层与输出面板的线程契约测试使用。
/// 不创建 WPF Application；Dispatcher 可以独立运行，因此测试不需要真实窗口。
/// </summary>
internal sealed class UiThread : IDisposable
{
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();

    public UiThread()
    {
        _thread = new Thread(() =>
        {
            Dispatcher = Dispatcher.CurrentDispatcher;
            _ready.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "ecl-app-tests-ui",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        if (!_ready.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new TimeoutException("测试 UI 线程未能在 10 秒内启动。");
        }
    }

    public Dispatcher Dispatcher { get; private set; } = null!;

    public void Invoke(Action action) => Dispatcher.Invoke(DispatcherPriority.Send, new Action(action));

    public T Invoke<T>(Func<T> func) => (T)Dispatcher.Invoke(DispatcherPriority.Send, new Func<T>(func));

    public Task InvokeAsync(Action action) => Dispatcher.InvokeAsync(action).Task;

    public Task<T> InvokeAsync<T>(Func<T> func) => Dispatcher.InvokeAsync(func).Task;

    /// <summary>等待 UI 线程执行完当前排队的 Normal 及更高优先级工作（含一次刷新调度）。</summary>
    public void Drain() => Dispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));

    public void Dispose()
    {
        Dispatcher.InvokeShutdown();
        _thread.Join(TimeSpan.FromSeconds(10));
        _ready.Dispose();
    }
}
