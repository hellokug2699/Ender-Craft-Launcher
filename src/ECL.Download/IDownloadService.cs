// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Download;

public interface IDownloadService
{
    Task<DownloadBatchResult> DownloadAsync(
        IReadOnlyList<DownloadRequest> requests,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<string> DownloadTextAsync(
        IReadOnlyList<Uri> urls,
        string? expectedHash = null,
        long? expectedSize = null,
        HashingAlgorithm algorithm = HashingAlgorithm.Sha1,
        CancellationToken cancellationToken = default);
}
