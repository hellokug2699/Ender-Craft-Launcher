// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.Extensions.Logging;

namespace Ecl.App.Shell.Output;

/// <summary>应用级输出行。时间、级别与来源用于区分可信系统事件和普通日志。</summary>
public sealed record AppLogEntry(DateTimeOffset Timestamp, LogLevel Level, string Category, string Message)
{
    public string TimeText => Timestamp.ToString("HH:mm:ss");

    /// <summary>技术性级别缩写，不承担本地化文案职责。</summary>
    public string LevelText => Level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "---",
    };
}

/// <summary>
/// 一次日志发布通知。条目与它所属的输出代次一起发布。
///
/// <see cref="Generation"/> 在与条目提交相同的锁区间内捕获，因此它精确表示
/// “这条日志属于哪一次清空之后”。订阅者即使延迟收到通知，也能判断它是否已被清空。
/// </summary>
public sealed record AppLogNotification(AppLogEntry Entry, long Generation);

/// <summary>
/// 有界内存日志缓冲：Output 面板只观察最近的应用日志，不替代文件日志或游戏进程日志会话。
///
/// 策略：容量固定为 <see cref="Capacity"/>，溢出时丢弃最旧条目并累计 <see cref="DroppedCount"/>，
/// 因此生产者持续写入时内存不会增长。写入与读取用同一把锁保护顺序；
/// <see cref="EntryWritten"/> 在锁外触发，订阅者不得假设多线程生产者的跨线程到达顺序。
///
/// 输出代次：每条通知都携带提交时代次（见 <see cref="AppLogNotification"/>）。
/// <see cref="BeginOutputGeneration"/> 在同一个锁区间内推进代次，这是“清空输出”与日志生产共享的
/// 同步点：在该调用返回之前提交的条目携带更小的代次，订阅者据此拒绝迟到通知。
/// 代次只增不减，因此订阅者用“小于当前代次”即可判定过期，不需要与清空操作共享锁。
/// </summary>
public sealed class AppLogBuffer : ILoggerProvider
{
    public const int Capacity = 2000;

    private readonly object _gate = new();
    private readonly Queue<AppLogEntry> _entries = new();
    private int _dropped;
    private long _generation;
    private bool _disposed;

    /// <summary>低于该级别的日志不进入面板；文件日志仍保留 Debug 级别。</summary>
    public LogLevel MinimumLevel { get; init; } = LogLevel.Information;

    public event EventHandler<AppLogNotification>? EntryWritten;

    /// <summary>因容量限制被丢弃的条目总数；用户清空输出时重置。</summary>
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

    /// <summary>当前输出代次。每次清空输出推进一次，并在通知中随条目一起发布。</summary>
    public long Generation
    {
        get
        {
            lock (_gate)
            {
                return _generation;
            }
        }
    }

    public IReadOnlyList<AppLogEntry> Snapshot()
    {
        lock (_gate)
        {
            return _entries.ToArray();
        }
    }

    public ILogger CreateLogger(string categoryName) => new BufferLogger(this, Shorten(categoryName));

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }
    }

    /// <summary>
    /// 清空输出：推进输出代次并重置截断计数。
    ///
    /// 这是与日志生产共享的同步点。调用返回后，任何此前提交的条目都带有更小的代次，
    /// 因此它的迟到通知可以被订阅者识别并拒绝；此后提交的条目携带本次返回的代次，会正常显示。
    /// </summary>
    public long BeginOutputGeneration()
    {
        lock (_gate)
        {
            _dropped = 0;
            _generation++;
            return _generation;
        }
    }

    internal bool IsEnabled(LogLevel level) => level != LogLevel.None && level >= MinimumLevel;

    internal void Write(AppLogEntry entry)
    {
        AppLogNotification notification;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (_entries.Count == Capacity)
            {
                _entries.Dequeue();
                _dropped++;
            }

            _entries.Enqueue(entry);

            // 代次必须与提交在同一锁区间内捕获，否则“清空”与“提交”之间会出现无法判定的窗口。
            notification = new AppLogNotification(entry, _generation);
        }

        // 锁外通知，避免日志订阅者在持锁期间执行任意代码。
        EntryWritten?.Invoke(this, notification);
    }

    private static string Shorten(string categoryName)
    {
        var name = categoryName;
        var generic = name.IndexOf('`');
        if (generic > 0)
        {
            name = name[..generic];
        }

        var lastDot = name.LastIndexOf('.');
        return lastDot >= 0 && lastDot < name.Length - 1 ? name[(lastDot + 1)..] : name;
    }

    private sealed class BufferLogger(AppLogBuffer owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => owner.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            if (exception is not null)
            {
                message = $"{message}{Environment.NewLine}{exception}";
            }

            owner.Write(new AppLogEntry(DateTimeOffset.Now, logLevel, category, message));
        }
    }
}
