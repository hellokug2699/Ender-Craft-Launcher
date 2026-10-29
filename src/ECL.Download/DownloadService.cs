// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Ecl.Configuration.Settings;
using Microsoft.Extensions.Logging;

namespace Ecl.Download;

public sealed class DownloadService(
    IHttpClientProvider httpClientProvider,
    ISettingsService settings,
    ILogger<DownloadService> logger) : IDownloadService, IDownloadController
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
        var totalBytes = uniqueRequests.Sum(request => request.Size ?? 0);
        var completedFiles = 0;
        var downloadedFiles = 0;
        var skippedFiles = 0;
        long completedBytes = 0;
        var failures = new ConcurrentBag<string>();

        var concurrency = Math.Clamp(settings.Get<int>("network.maxConcurrentDownloads"), 1, 64);
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = concurrency,
            CancellationToken = cancellationToken,
        };

        using var client = httpClientProvider.CreateClient();
        var speedWatch = Stopwatch.StartNew();
        double Speed(long bytes) => speedWatch.Elapsed.TotalSeconds < 0.2
            ? 0
            : bytes / speedWatch.Elapsed.TotalSeconds;

        Report(progress, 0, totalFiles, 0, totalBytes, null, 0, IsPaused);

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
                    Report(
                        progress,
                        Volatile.Read(ref completedFiles),
                        totalFiles,
                        Volatile.Read(ref completedBytes),
                        totalBytes,
                        displayName,
                        Speed(Volatile.Read(ref completedBytes)),
                        IsPaused);
                    return;
                }

                await DownloadOneAsync(client, request, token).ConfigureAwait(false);
                Interlocked.Increment(ref downloadedFiles);
                Interlocked.Increment(ref completedFiles);
                Interlocked.Add(ref completedBytes, request.Size ?? GetFileLength(request.DestinationPath));
                Report(
                    progress,
                    Volatile.Read(ref completedFiles),
                    totalFiles,
                    Volatile.Read(ref completedBytes),
                    totalBytes,
                    displayName,
                    Speed(Volatile.Read(ref completedBytes)),
                    IsPaused);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                failures.Add($"{displayName}: {exception.Message}");
                logger.LogError(exception, "下载失败：{File}", request.DestinationPath);
            }
        }).ConfigureAwait(false);

        if (!failures.IsEmpty)
        {
            throw new DownloadException(
                $"有 {failures.Count} 个文件下载失败。",
                failures.ToArray());
        }

        return new DownloadBatchResult(
            Volatile.Read(ref downloadedFiles),
            Volatile.Read(ref skippedFiles),
            Volatile.Read(ref completedBytes));
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

        throw new DownloadException("下载文本失败。", failures);
    }

    private async Task DownloadOneAsync(HttpClient client, DownloadRequest request, CancellationToken cancellationToken)
    {
        var destinationDirectory = Path.GetDirectoryName(request.DestinationPath);
        if (!string.IsNullOrEmpty(destinationDirectory))
        {
            Directory.CreateDirectory(destinationDirectory);
        }

        if (request.Urls.Count == 0)
        {
            throw new DownloadException("该文件没有可用的下载地址。", [request.DisplayName ?? request.DestinationPath]);
        }

        var partPath = request.DestinationPath + ".part";
        var failures = new List<string>();

        foreach (var url in request.Urls)
        {
            for (var attempt = 0; attempt < MaxAttemptsPerUrl; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await DownloadFromUrlAsync(client, url, request, partPath, cancellationToken).ConfigureAwait(false);
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

        throw new DownloadException($"下载失败：{request.DisplayName ?? Path.GetFileName(request.DestinationPath)}", failures);
    }

    private async Task DownloadFromUrlAsync(
        HttpClient client,
        Uri url,
        DownloadRequest request,
        string partPath,
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
                    _resumeSignal.Wait(token);
                    var read = await source.ReadAsync(buffer.AsMemory(0, BufferSize), token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    await destination.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
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

    private static long GetFileLength(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

    private static void Report(
        IProgress<DownloadProgress>? progress,
        int completedFiles,
        int totalFiles,
        long completedBytes,
        long totalBytes,
        string? currentFile,
        double bytesPerSecond,
        bool isPaused) =>
        progress?.Report(new DownloadProgress(completedFiles, totalFiles, completedBytes, totalBytes, currentFile)
        {
            BytesPerSecond = bytesPerSecond,
            IsPaused = isPaused,
        });
}
