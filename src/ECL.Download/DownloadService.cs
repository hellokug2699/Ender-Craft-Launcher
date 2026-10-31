// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Ecl.Configuration.Settings;
using Ecl.Operations;
using Microsoft.Extensions.Logging;

namespace Ecl.Download;

public sealed class DownloadService(
    IHttpClientProvider httpClientProvider,
    ISettingsService settings,
    ILogger<DownloadService> logger,
    IOperationService? operations = null) : IDownloadService, IDownloadController
{
    private const int BufferSize = 81920;
    private const int MaxAttemptsPerUrl = 3;
    private static readonly TimeSpan FileDownloadTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan TextDownloadTimeout = TimeSpan.FromMinutes(2);

    private readonly ManualResetEventSlim _resumeSignal = new(initialState: true);

    public bool IsPaused => !_resumeSignal.IsSet;

    public void Pause() => _resumeSignal.Reset();

    public void Resume() => _resumeSignal.Set();

    public async Task<DownloadBatchResult> DownloadAsync(
        IReadOnlyList<DownloadRequest> requests,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var uniqueRequests = requests
            .GroupBy(request => request.DestinationPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();

        if (uniqueRequests.Length == 0)
        {
            return new DownloadBatchResult(0, 0, 0);
        }

        var totalFiles = uniqueRequests.Length;
        var knownTotalBytes = uniqueRequests.Sum(request => request.Size ?? 0);
        var allSizesKnown = uniqueRequests.All(request => request.Size.HasValue);
        var completedFiles = 0;
        var downloadedFiles = 0;
        var skippedFiles = 0;
        long completedBytes = 0;
        var failures = new ConcurrentBag<string>();
        var failureKinds = new ConcurrentBag<OperationErrorKind>();

        var concurrency = Math.Clamp(settings.Get<int>("network.maxConcurrentDownloads"), 1, 64);
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = concurrency,
            CancellationToken = cancellationToken,
        };

        using var client = httpClientProvider.CreateClient();
        using var tracker = new DownloadBatchTracker(
            progress,
            operations,
            uniqueRequests,
            knownTotalBytes,
            allSizesKnown,
            () => IsPaused);

        tracker.ReportInitial();

        try
        {
            await Parallel.ForEachAsync(uniqueRequests, parallelOptions, async (request, token) =>
            {
                var displayName = request.DisplayName ?? Path.GetFileName(request.DestinationPath);

                try
                {
                    if (await IsExistingValidAsync(request, token).ConfigureAwait(false))
                    {
                        var size = request.Size ?? GetFileLength(request.DestinationPath);
                        Interlocked.Increment(ref skippedFiles);
                        Interlocked.Increment(ref completedFiles);
                        Interlocked.Add(ref completedBytes, size);
                        tracker.FileCompleted(displayName, size, skipped: true);
                        return;
                    }

                    await DownloadOneAsync(client, request, tracker, token).ConfigureAwait(false);
                    Interlocked.Increment(ref downloadedFiles);
                    Interlocked.Increment(ref completedFiles);
                    var completedSize = request.Size ?? GetFileLength(request.DestinationPath);
                    Interlocked.Add(ref completedBytes, completedSize);
                    tracker.FileCompleted(displayName, completedSize, skipped: false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    failures.Add($"{displayName}: {exception.Message}");
                    failureKinds.Add(exception is DownloadException download
                        ? download.Kind
                        : ClassifyFailure(exception, cancellationToken));
                    logger.LogError(exception, "下载失败：{File}", request.DestinationPath);
                }
            }).ConfigureAwait(false);

            if (!failures.IsEmpty)
            {
                throw new DownloadException(
                    $"有 {failures.Count} 个文件下载失败。",
                    failures.ToArray())
                {
                    Kind = OperationErrors.Worst(failureKinds),
                };
            }

            tracker.Complete();
            return new DownloadBatchResult(
                Volatile.Read(ref downloadedFiles),
                Volatile.Read(ref skippedFiles),
                Volatile.Read(ref completedBytes));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            tracker.Cancel();
            throw;
        }
        catch (DownloadException exception)
        {
            tracker.Fail(new OperationError(
                exception.Kind,
                "download.failed",
                exception.Message,
                exception.Failures.FirstOrDefault()));
            throw;
        }
        catch (Exception exception)
        {
            tracker.Fail(OperationErrors.FromException("download.failed", exception));
            throw;
        }
    }

    public async Task<string> DownloadTextAsync(
        IReadOnlyList<Uri> urls,
        string? expectedHash = null,
        long? expectedSize = null,
        HashingAlgorithm algorithm = HashingAlgorithm.Sha1,
        CancellationToken cancellationToken = default)
    {
        if (urls.Count == 0)
        {
            throw new DownloadException("没有可用的下载地址。", []);
        }

        using var client = httpClientProvider.CreateClient();
        var failures = new List<string>();

        foreach (var url in urls)
        {
            for (var attempt = 0; attempt < MaxAttemptsPerUrl; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _resumeSignal.Wait(cancellationToken);
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attemptCts.CancelAfter(TextDownloadTimeout);
                try
                {
                    using var response = await client.GetAsync(
                            url,
                            HttpCompletionOption.ResponseHeadersRead,
                            attemptCts.Token)
                        .ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new HttpRequestException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
                    }

                    var bytes = await response.Content.ReadAsByteArrayAsync(attemptCts.Token).ConfigureAwait(false);
                    if (expectedSize.HasValue && bytes.Length != expectedSize.Value)
                    {
                        throw new InvalidDataException($"大小校验失败：期望 {expectedSize.Value}，实际 {bytes.Length}。");
                    }

                    if (!string.IsNullOrWhiteSpace(expectedHash))
                    {
                        var actual = ComputeHash(bytes, algorithm);
                        if (!string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidDataException("哈希校验失败。");
                        }
                    }

                    return Encoding.UTF8.GetString(bytes);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    failures.Add($"{url}: {exception.Message}");
                    logger.LogDebug(exception, "读取文本失败：{Url}（第 {Attempt} 次）", url, attempt + 1);
                    if (!IsRetryable(exception))
                    {
                        break;
                    }

                    if (attempt + 1 < MaxAttemptsPerUrl)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(400 * (attempt + 1)), cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }

        throw new DownloadException("下载文本失败。", failures)
        {
            Kind = OperationErrorKind.Upstream,
        };
    }

    private async Task DownloadOneAsync(
        HttpClient client,
        DownloadRequest request,
        DownloadBatchTracker tracker,
        CancellationToken cancellationToken)
    {
        var destinationDirectory = Path.GetDirectoryName(request.DestinationPath);
        if (!string.IsNullOrEmpty(destinationDirectory))
        {
            Directory.CreateDirectory(destinationDirectory);
        }

        var displayName = request.DisplayName ?? Path.GetFileName(request.DestinationPath);
        if (request.Urls.Count == 0)
        {
            throw new DownloadException("该文件没有可用的下载地址。", [displayName])
            {
                Kind = OperationErrorKind.Configuration,
            };
        }

        var partPath = request.DestinationPath + ".part";
        var failures = new List<string>();
        var kind = OperationErrorKind.EclInternal;

        foreach (var url in request.Urls)
        {
            for (var attempt = 0; attempt < MaxAttemptsPerUrl; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await DownloadFromUrlAsync(client, url, request, partPath, tracker, displayName, cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    failures.Add($"{url}: {exception.Message}");
                    logger.LogDebug(exception, "下载失败：{Url}（第 {Attempt} 次）", url, attempt + 1);
                    if (kind == OperationErrorKind.EclInternal)
                    {
                        kind = ClassifyFailure(exception, cancellationToken);
                    }

                    if (!IsRetryable(exception))
                    {
                        break;
                    }

                    if (attempt + 1 < MaxAttemptsPerUrl)
                    {
                        // 让任务中心显示“正在重试”，而不是把可重试的失败直接当成最终失败。
                        tracker.Retry(displayName, attempt + 1, MaxAttemptsPerUrl, exception.Message);
                        await Task.Delay(
                                TimeSpan.FromMilliseconds(400 * (attempt + 1)),
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
            }
        }

        throw new DownloadException($"下载失败：{displayName}", failures)
        {
            Kind = kind,
        };
    }

    private async Task DownloadFromUrlAsync(
        HttpClient client,
        Uri url,
        DownloadRequest request,
        string partPath,
        DownloadBatchTracker tracker,
        string displayName,
        CancellationToken cancellationToken)
    {
        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attemptCts.CancelAfter(FileDownloadTimeout);
        var token = attemptCts.Token;

        var existingLength = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
        if (request.Size.HasValue && existingLength > request.Size.Value)
        {
            File.Delete(partPath);
            existingLength = 0;
        }

        using var message = new HttpRequestMessage(HttpMethod.Get, url);
        if (existingLength > 0)
        {
            message.Headers.Range = new RangeHeaderValue(existingLength, null);
        }

        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && existingLength > 0)
        {
            if (await IsValidFileAsync(partPath, request, token).ConfigureAwait(false))
            {
                File.Move(partPath, request.DestinationPath, overwrite: true);
                return;
            }

            File.Delete(partPath);
            throw new InvalidDataException("断点文件无效，已重新开始下载。");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        var append = response.StatusCode == HttpStatusCode.PartialContent && existingLength > 0;
        var contentLength = response.Content.Headers.ContentLength;
        var totalBytes = request.Size ??
                         (contentLength.HasValue ? existingLength + contentLength.Value : null);
        var written = append ? existingLength : 0;
        tracker.FileStarted(displayName, totalBytes, written);

        await using (var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
        await using (var destination = new FileStream(
                         partPath,
                         append ? FileMode.Append : FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         BufferSize,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                while (true)
                {
                    // 暂停点放在每次读写之间，避免暂停时仍把当前响应体灌进磁盘。
                    if (IsPaused)
                    {
                        tracker.ForceReport();
                    }

                    _resumeSignal.Wait(token);
                    var read = await source.ReadAsync(buffer.AsMemory(0, BufferSize), token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    await destination.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    written += read;
                    tracker.FileProgress(displayName, written);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            await destination.FlushAsync(token).ConfigureAwait(false);
        }

        if (!await IsValidFileAsync(partPath, request, token, acceptUnknown: true).ConfigureAwait(false))
        {
            File.Delete(partPath);
            throw new InvalidDataException("大小或哈希校验失败。");
        }

        File.Move(partPath, request.DestinationPath, overwrite: true);
    }

    private static async Task<bool> IsExistingValidAsync(DownloadRequest request, CancellationToken cancellationToken)
    {
        if (!File.Exists(request.DestinationPath))
        {
            return false;
        }

        return await IsValidFileAsync(request.DestinationPath, request, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> IsValidFileAsync(
        string path,
        DownloadRequest request,
        CancellationToken cancellationToken,
        bool acceptUnknown = false)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        var length = new FileInfo(path).Length;
        if (request.Size.HasValue && length != request.Size.Value)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.Hash))
        {
            return request.Size.HasValue || acceptUnknown;
        }

        var actual = await ComputeFileHashAsync(path, request.Algorithm, cancellationToken).ConfigureAwait(false);
        return string.Equals(actual, request.Hash, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> ComputeFileHashAsync(string path, HashingAlgorithm algorithm, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(ToHashAlgorithmName(algorithm));
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);

        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer, 0, read);
            }

            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static string ComputeHash(ReadOnlySpan<byte> data, HashingAlgorithm algorithm) =>
        Convert.ToHexString(algorithm == HashingAlgorithm.Sha1 ? SHA1.HashData(data) : SHA256.HashData(data))
            .ToLowerInvariant();

    private static HashAlgorithmName ToHashAlgorithmName(HashingAlgorithm algorithm) =>
        algorithm == HashingAlgorithm.Sha1 ? HashAlgorithmName.SHA1 : HashAlgorithmName.SHA256;

    private static bool IsRetryable(Exception exception) => exception switch
    {
        HttpRequestException http => http.StatusCode is null ||
                                     (int)http.StatusCode >= 500 ||
                                     http.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests,
        IOException => true,
        InvalidDataException => true,
        // HttpClient 连接超时会以 TaskCanceledException 形式抛出，用户取消已在上层过滤。
        OperationCanceledException => true,
        TimeoutException => true,
        _ => false,
    };

    /// <summary>
    /// HTTP 超时同样表现为 <see cref="OperationCanceledException"/>；只有用户主动取消才算 Cancelled，
    /// 否则按网络问题归因，避免任务中心把超时显示成“已取消”。
    /// </summary>
    private static OperationErrorKind ClassifyFailure(Exception exception, CancellationToken userToken) =>
        exception is OperationCanceledException && !userToken.IsCancellationRequested
            ? OperationErrorKind.Network
            : OperationErrors.Classify(exception);

    private static long GetFileLength(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

    /// <summary>
    /// 把一个下载批次的进度同时写入旧 <see cref="DownloadProgress"/> 与统一任务中心。
    /// 进度按时间节流，避免每读 80 KB 就触发一次 UI 更新。
    /// </summary>
    private sealed class DownloadBatchTracker : IDisposable
    {
        private const int ReportIntervalMilliseconds = 150;

        private readonly IProgress<DownloadProgress>? _progress;
        private readonly IOperationHandle? _operation;
        private readonly Func<bool> _isPaused;
        private readonly int _totalFiles;
        private readonly SpeedMeter _speed = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly object _gate = new();
        private readonly Dictionary<string, long> _activeBytes = new(StringComparer.OrdinalIgnoreCase);
        private long _knownTotalBytes;
        private bool _allSizesKnown;
        private long _completedBytes;
        private int _completedFiles;
        private string? _currentItem;
        private long _currentItemBytes;
        private long? _currentItemTotalBytes;
        private long _lastReportMilliseconds = -ReportIntervalMilliseconds;
        private long _lastSpeedBytes = -1;
        private int _retryAttempt;
        private int _retryMaxAttempts;
        private string? _retryReason;
        private bool _disposed;

        public DownloadBatchTracker(
            IProgress<DownloadProgress>? progress,
            IOperationService? operations,
            IReadOnlyList<DownloadRequest> requests,
            long knownTotalBytes,
            bool allSizesKnown,
            Func<bool> isPaused)
        {
            _progress = progress;
            _isPaused = isPaused;
            _totalFiles = requests.Count;
            _knownTotalBytes = knownTotalBytes;
            _allSizesKnown = allSizesKnown;
            _operation = operations?.Begin(new OperationStart
            {
                Kind = OperationKind.Download,
                Title = BuildTitle(requests),
                StageId = "download",
                Source = BuildSource(requests),
                Total = requests.Count,
                TotalBytes = allSizesKnown ? knownTotalBytes : null,
                CanCancel = true,
                CanRetry = true,
            });
        }

        public void ReportInitial()
        {
            lock (_gate)
            {
                PublishLocked();
            }
        }

        public void FileStarted(string file, long? totalBytes, long initialBytes)
        {
            lock (_gate)
            {
                _activeBytes[file] = initialBytes;
                _currentItem = file;
                _currentItemBytes = initialBytes;
                _currentItemTotalBytes = totalBytes;

                // 单文件且大小来自 Content-Length 时，总大小从未知升级为确定。
                if (!_allSizesKnown && _totalFiles == 1 && totalBytes.HasValue)
                {
                    _knownTotalBytes = totalBytes.Value;
                    _allSizesKnown = true;
                }

                ClearRetryLocked();
                PublishLocked();
            }
        }

        public void FileProgress(string file, long bytes)
        {
            lock (_gate)
            {
                _activeBytes[file] = bytes;
                _currentItem = file;
                _currentItemBytes = bytes;
                ClearRetryLocked();

                if (_clock.ElapsedMilliseconds - _lastReportMilliseconds >= ReportIntervalMilliseconds)
                {
                    PublishLocked();
                }
            }
        }

        public void FileCompleted(string file, long size, bool skipped)
        {
            lock (_gate)
            {
                _activeBytes.Remove(file);
                _completedBytes += size;
                _completedFiles++;
                _currentItem = file;
                _currentItemBytes = size;
                _currentItemTotalBytes = size;
                ClearRetryLocked();
                PublishLocked();
            }
        }

        public void Retry(string file, int attempt, int maxAttempts, string? reason)
        {
            lock (_gate)
            {
                _retryAttempt = attempt;
                _retryMaxAttempts = maxAttempts;
                _retryReason = reason;
                _currentItem = file;
                PublishLocked();
            }

            // 进度发布后进入 Retrying，避免下一次进度上报把它立刻覆盖成 Running。
            _operation?.Retrying(attempt, maxAttempts, reason);
        }

        public void ForceReport()
        {
            lock (_gate)
            {
                PublishLocked();
            }
        }

        public void Complete()
        {
            lock (_gate)
            {
                PublishLocked();
            }

            _operation?.Complete();
        }

        public void Fail(OperationError error) => _operation?.Fail(error);

        public void Cancel() => _operation?.Cancel();

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _operation?.Dispose();
        }

        private void PublishLocked()
        {
            var activeBytes = _activeBytes.Values.Sum();
            var downloaded = _completedBytes + activeBytes;

            double? speed;
            if (downloaded != _lastSpeedBytes)
            {
                speed = _speed.Sample(downloaded);
                _lastSpeedBytes = downloaded;
            }
            else
            {
                speed = _speed.BytesPerSecond;
            }

            var paused = _isPaused();
            _progress?.Report(new DownloadProgress(
                _completedFiles,
                _totalFiles,
                downloaded,
                _knownTotalBytes,
                _currentItem)
            {
                BytesPerSecond = speed ?? 0,
                IsPaused = paused,
                IsTotalBytesKnown = _allSizesKnown,
                CurrentFileBytes = _currentItemBytes,
                CurrentFileTotalBytes = _currentItemTotalBytes,
                RetryAttempt = _retryAttempt,
                MaxRetryAttempts = _retryMaxAttempts,
                RetryReason = _retryReason,
            });

            _operation?.Report(new OperationProgressUpdate
            {
                StageId = "download",
                CurrentItem = _currentItem,
                Completed = _completedFiles,
                Total = _totalFiles,
                BytesDownloaded = downloaded,
                TotalBytes = _allSizesKnown ? _knownTotalBytes : null,
                BytesPerSecond = speed,
                IsPaused = paused,
            });

            _lastReportMilliseconds = _clock.ElapsedMilliseconds;
        }

        private void ClearRetryLocked()
        {
            _retryAttempt = 0;
            _retryMaxAttempts = 0;
            _retryReason = null;
        }

        private static string BuildTitle(IReadOnlyList<DownloadRequest> requests)
        {
            var first = requests[0].DisplayName ?? Path.GetFileName(requests[0].DestinationPath);
            return requests.Count == 1 ? first : $"{first} (+{requests.Count - 1})";
        }

        private static string? BuildSource(IReadOnlyList<DownloadRequest> requests) =>
            requests
                .SelectMany(request => request.Urls)
                .Select(uri => uri.Host)
                .FirstOrDefault(host => !string.IsNullOrWhiteSpace(host));
    }
}
