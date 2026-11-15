// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.App.Shell.Output;

namespace Ecl.App.ViewModels.Home;

/// <summary>
/// 启动期间的轻量实时输出采集（home.md §3.2 高级模式、logs.md §7.1）：
/// 只在启动进行中订阅应用级有界日志缓冲，把条目放进有界队列；
/// 它不替代 ECL Output 面板，也不替代 Logs Workspace 的会话证据。
///
/// 线程契约：日志事件可能来自任意线程，本类只用锁保护自己的队列，不触碰 UI 集合；
/// 调用方在 UI 线程 Drain() 后自行追加。
/// </summary>
public sealed class HomeLaunchLogCapture : IDisposable
{
    public const int Capacity = 500;

    private readonly AppLogBuffer _buffer;
    private readonly object _gate = new();
    private readonly Queue<AppLogEntry> _pending = new();
    private int _dropped;
    private bool _active;
    private bool _disposed;

    public HomeLaunchLogCapture(AppLogBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        _buffer = buffer;
    }

    /// <summary>因容量上限被丢弃的行数；新一次启动开始时归零。</summary>
    public int DroppedCount
    {
        get
        {
            lock (_gate)
            {
                return _dropped;
            }
        }
    }

    /// <summary>开始一次启动采集：清空旧队列并订阅日志缓冲。</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _active)
            {
                return;
            }

            _pending.Clear();
            _dropped = 0;
            _active = true;
            _buffer.EntryWritten += OnEntryWritten;
        }
    }

    /// <summary>结束采集；已采集但未取走的条目仍可通过 <see cref="Drain"/> 取到。</summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (!_active)
            {
                return;
            }

            _active = false;
            _buffer.EntryWritten -= OnEntryWritten;
        }
    }

    /// <summary>取走当前待呈现的条目（按到达顺序）。</summary>
    public IReadOnlyList<AppLogEntry> Drain()
    {
        lock (_gate)
        {
            if (_pending.Count == 0)
            {
                return [];
            }

            var entries = _pending.ToArray();
            _pending.Clear();
            return entries;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_active)
            {
                _active = false;
                _buffer.EntryWritten -= OnEntryWritten;
            }

            _pending.Clear();
        }
    }

    private void OnEntryWritten(object? sender, AppLogNotification notification)
    {
        lock (_gate)
        {
            if (!_active || _disposed)
            {
                return;
            }

            if (_pending.Count >= Capacity)
            {
                _pending.Dequeue();
                _dropped++;
            }

            _pending.Enqueue(notification.Entry);
        }
    }
}
