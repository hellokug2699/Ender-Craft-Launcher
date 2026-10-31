// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Operations;

namespace Ecl.Download;

public enum HashingAlgorithm
{
    Sha1,
    Sha256,
}

public sealed record DownloadRequest(
    string DestinationPath,
    IReadOnlyList<Uri> Urls,
    string? Hash = null,
    HashingAlgorithm Algorithm = HashingAlgorithm.Sha1,
    long? Size = null,
    string? DisplayName = null);

public sealed record DownloadProgress(
    int CompletedFiles,
    int TotalFiles,
    long CompletedBytes,
    long TotalBytes,
    string? CurrentFile)
{
    public bool IsIndeterminate => TotalFiles == 0;

    /// <summary>总大小是否来自确定的文件清单；为 false 时不应显示百分比。</summary>
    public bool IsTotalBytesKnown { get; init; } = true;

    public double BytesPerSecond { get; init; }

    public bool IsPaused { get; init; }

    /// <summary>当前文件已写入的字节数（包含断点续传的既有部分）。</summary>
    public long CurrentFileBytes { get; init; }

    /// <summary>当前文件总大小；来自请求清单或 HTTP Content-Length，未知时为 null。</summary>
    public long? CurrentFileTotalBytes { get; init; }

    /// <summary>正在重试时大于 0；0 表示正常进度。</summary>
    public int RetryAttempt { get; init; }

    public int MaxRetryAttempts { get; init; }

    public string? RetryReason { get; init; }
}

/// <summary>全局下载暂停/恢复控制，供 UI 与下载服务共享。</summary>
public interface IDownloadController
{
    bool IsPaused { get; }

    void Pause();

    void Resume();
}

public sealed record DownloadBatchResult(int DownloadedFiles, int SkippedFiles, long Bytes);

public sealed class DownloadException : Exception
{
    public IReadOnlyList<string> Failures { get; }

    /// <summary>聚合失败的主要归因，供任务中心展示而不是解析异常文本。</summary>
    public OperationErrorKind Kind { get; init; } = OperationErrorKind.EclInternal;

    public DownloadException(string message, IReadOnlyList<string> failures, Exception? innerException = null)
        : base(message, innerException)
    {
        Failures = failures;
    }
}
