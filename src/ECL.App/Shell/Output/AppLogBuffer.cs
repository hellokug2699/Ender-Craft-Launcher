// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
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
/// 有界内存日志缓冲：Output 面板只观察最近的应用日志，不替代文件日志或游戏进程日志会话。
/// 容量固定，避免长时间运行导致 UI 内存持续增长。
/// </summary>
public sealed class AppLogBuffer : ILoggerProvider
{
    public const int Capacity = 2000;

    private readonly ConcurrentQueue<AppLogEntry> _entries = new();

    /// <summary>低于该级别的日志不进入面板；文件日志仍保留 Debug 级别。</summary>
    public LogLevel MinimumLevel { get; init; } = LogLevel.Information;

    public event EventHandler<AppLogEntry>? EntryWritten;

    public IReadOnlyList<AppLogEntry> Snapshot() => _entries.ToArray();

    public ILogger CreateLogger(string categoryName) => new BufferLogger(this, Shorten(categoryName));

    public void Dispose()
    {
        // 缓冲区不持有外部资源；Dispose 只停止接收新行。
    }

    internal bool IsEnabled(LogLevel level) => level != LogLevel.None && level >= MinimumLevel;

    internal void Write(AppLogEntry entry)
    {
        _entries.Enqueue(entry);
        while (_entries.Count > Capacity && _entries.TryDequeue(out _))
        {
        }

        EntryWritten?.Invoke(this, entry);
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
