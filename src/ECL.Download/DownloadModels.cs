// SPDX-License-Identifier: GPL-3.0-or-later

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

    public double BytesPerSecond { get; init; }

    public bool IsPaused { get; init; }
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

    public DownloadException(string message, IReadOnlyList<string> failures, Exception? innerException = null)
        : base(message, innerException)
    {
        Failures = failures;
    }
}
