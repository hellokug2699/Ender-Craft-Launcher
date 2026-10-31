// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Download;

/// <summary>
/// 全局下载任务的生命周期。UI 不应自行推断下载状态。
/// </summary>
public enum DownloadTaskState
{
    Queued,
    Downloading,
    Paused,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>
/// 面向应用层的下载任务快照。
/// 下载执行器负责产生状态，UI 只负责观察和呈现。
/// </summary>
public sealed record DownloadTask(
    Guid Id,
    string Name,
    DownloadTaskState State,
    long DownloadedBytes,
    long? TotalBytes,
    double? BytesPerSecond,
    string? Source,
    string? Destination,
    string? Error,
    DateTimeOffset CreatedAt)
{
    public double? Progress => TotalBytes is > 0
        ? Math.Clamp((double)DownloadedBytes / TotalBytes.Value, 0, 1)
        : null;
}

public sealed class DownloadTaskChangedEventArgs(DownloadTask task) : EventArgs
{
    public DownloadTask Task { get; } = task;
}

/// <summary>
/// 全局下载任务管理器的最小契约。
/// 具体执行逻辑仍由现有 IDownloadService 负责，后续逐步迁移到这里。
/// </summary>
public interface IDownloadTaskManager
{
    IReadOnlyList<DownloadTask> Tasks { get; }

    event EventHandler<DownloadTaskChangedEventArgs>? TaskChanged;

    Task<DownloadTask> EnqueueAsync(
        DownloadRequest request,
        CancellationToken cancellationToken = default);

    Task PauseAsync(Guid taskId, CancellationToken cancellationToken = default);

    Task ResumeAsync(Guid taskId, CancellationToken cancellationToken = default);

    Task CancelAsync(Guid taskId, CancellationToken cancellationToken = default);
}
