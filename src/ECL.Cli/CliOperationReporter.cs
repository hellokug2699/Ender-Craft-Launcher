// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core.Localization;
using Ecl.Operations;

namespace Ecl.Cli;

/// <summary>
/// 命令行任务进度：订阅 <see cref="IOperationStore"/>，只消费快照，
/// 不依赖某个具体业务服务（安装、整合包、Java、CurseForge）。
/// <para>
/// 进度写入标准错误，保证 <c>--json</c> 的标准输出仍然可用作机器读取。
/// 阶段变化立即输出，纯进度与 ETA 每秒刷新一次，避免刷屏。
/// </para>
/// </summary>
public sealed class CliOperationReporter : IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);

    private readonly IOperationStore _store;
    private readonly ILocalizer _localizer;
    private readonly TextWriter _writer;
    private readonly object _gate = new();
    private Guid? _trackedId;
    private string? _lastStage;
    private long _lastRefreshTicks;
    private bool _disposed;

    public CliOperationReporter(IOperationStore store, ILocalizer localizer, TextWriter? writer = null)
    {
        _store = store;
        _localizer = localizer;
        _writer = writer ?? Console.Error;
        _store.Changed += OnChanged;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _store.Changed -= OnChanged;
    }

    private void OnChanged(object? sender, OperationSnapshot snapshot)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (snapshot.IsTerminal)
            {
                if (snapshot.Id == _trackedId)
                {
                    ReportFailure(snapshot);
                }

                return;
            }

            if (!Track(snapshot))
            {
                return;
            }

            Render(snapshot);
        }
    }

    /// <summary>
    /// 跟踪第一个开始的任务；它结束后允许跟踪后续任务（例如失败后的重试）。
    /// </summary>
    private bool Track(OperationSnapshot snapshot)
    {
        if (_trackedId is not { } tracked)
        {
            _trackedId = snapshot.Id;
            return true;
        }

        if (tracked == snapshot.Id)
        {
            return true;
        }

        var current = _store.TryGet(tracked);
        if (current is { IsTerminal: false })
        {
            return false;
        }

        _trackedId = snapshot.Id;
        _lastStage = null;
        _lastRefreshTicks = 0;
        return true;
    }

    private void Render(OperationSnapshot snapshot)
    {
        if (snapshot.State == OperationState.Retrying)
        {
            _writer.WriteLine("  " + _localizer.Get(
                "operations.retry",
                snapshot.RetryAttempt,
                snapshot.MaxRetryAttempts,
                snapshot.RetryReason ?? string.Empty));
            return;
        }

        var stage = StageTextFor(snapshot);
        if (stage.Length > 0 && !string.Equals(stage, _lastStage, StringComparison.Ordinal))
        {
            _writer.WriteLine(stage);
            _lastStage = stage;
            _lastRefreshTicks = 0;
        }

        var now = Environment.TickCount64;
        if (now - _lastRefreshTicks < RefreshInterval.TotalMilliseconds)
        {
            return;
        }

        _lastRefreshTicks = now;
        var line = DescribeProgress(snapshot);
        if (line.Length > 0)
        {
            _writer.WriteLine("  " + line);
        }
    }

    private void ReportFailure(OperationSnapshot snapshot)
    {
        if (snapshot.State != OperationState.Failed || snapshot.Error is not { } error)
        {
            return;
        }

        var reason = _localizer.Get($"operations.error.{error.Kind.ToString().ToLowerInvariant()}");
        _writer.WriteLine("  " + _localizer.Get("operations.failedReason", reason, error.Message));
    }

    private string StageTextFor(OperationSnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.StageId))
        {
            var key = $"operations.stage.{snapshot.StageId.ToLowerInvariant()}";
            var text = _localizer.Get(key);
            if (!string.Equals(text, key, StringComparison.Ordinal))
            {
                return text;
            }
        }

        return snapshot.StageText ?? string.Empty;
    }

    private string DescribeProgress(OperationSnapshot snapshot)
    {
        var parts = new List<string>(4);
        if (snapshot.Progress is { } bytesProgress)
        {
            parts.Add(
                $"{bytesProgress * 100:F0}% · {OperationFormat.Bytes(snapshot.BytesDownloaded)} / {OperationFormat.Bytes(snapshot.TotalBytes ?? 0)}");
        }
        else if (snapshot.ItemProgress is { } itemProgress)
        {
            parts.Add(
                _localizer.Get("operations.items", snapshot.Completed, snapshot.Total ?? 0) +
                $" ({itemProgress * 100:F0}%)");
        }
        else if (snapshot.BytesDownloaded > 0)
        {
            parts.Add(OperationFormat.Bytes(snapshot.BytesDownloaded));
        }

        if (snapshot.BytesPerSecond is > 0)
        {
            parts.Add(OperationFormat.Speed(snapshot.BytesPerSecond.Value));
        }

        if (snapshot.Eta is { } eta)
        {
            parts.Add(_localizer.Get("operations.eta", FormatDuration(eta)));
        }

        return string.Join(" · ", parts);
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}";
        }

        return duration.TotalMinutes >= 1
            ? $"{(int)duration.TotalMinutes}:{duration.Seconds:00}"
            : $"{Math.Max(0, (int)Math.Ceiling(duration.TotalSeconds))}s";
    }
}
