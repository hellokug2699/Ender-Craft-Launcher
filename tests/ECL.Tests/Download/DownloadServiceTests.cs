// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using System.Security.Cryptography;
using System.Text;
using Ecl.Configuration.Settings;
using Ecl.Download;
using Ecl.Operations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ecl.Tests.Download;

public sealed class DownloadServiceTests
{
    [Fact]
    public async Task DownloadsVerifiesAndSkipsExistingFile()
    {
        using var environment = new TestEnvironment();
        using var provider = environment.CreateServices();
        var settings = provider.GetRequiredService<ISettingsService>();
        var handler = new StubHandler();
        var service = CreateService(settings, handler);

        var content = Encoding.UTF8.GetBytes("hello world");
        var hash = Convert.ToHexString(SHA1.HashData(content)).ToLowerInvariant();
        handler.Respond = _ => Ok(content);

        var destination = Path.Combine(environment.Root, "downloads", "file.bin");
        var request = new DownloadRequest(
            destination,
            [new Uri("https://example.com/file.bin")],
            hash,
            HashingAlgorithm.Sha1,
            content.Length);

        var first = await service.DownloadAsync([request]);
        Assert.Equal(1, first.DownloadedFiles);
        Assert.Equal("hello world", await File.ReadAllTextAsync(destination));

        var second = await service.DownloadAsync([request]);
        Assert.Equal(1, second.SkippedFiles);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task RejectsHashMismatchAndCleansPartFile()
    {
        using var environment = new TestEnvironment();
        using var provider = environment.CreateServices();
        var settings = provider.GetRequiredService<ISettingsService>();
        var handler = new StubHandler();
        var operations = new OperationService();
        var service = CreateService(settings, handler, operations);

        var good = Encoding.UTF8.GetBytes("good");
        var bad = Encoding.UTF8.GetBytes("bad");
        var hash = Convert.ToHexString(SHA1.HashData(good)).ToLowerInvariant();
        handler.Respond = _ => Ok(bad);

        var destination = Path.Combine(environment.Root, "downloads", "bad.bin");
        var request = new DownloadRequest(
            destination,
            [new Uri("https://example.com/bad.bin")],
            hash,
            HashingAlgorithm.Sha1,
            bad.Length);

        await Assert.ThrowsAsync<DownloadException>(() => service.DownloadAsync([request]));
        Assert.False(File.Exists(destination));
        Assert.False(File.Exists(destination + ".part"));

        var snapshot = Assert.Single(operations.Snapshots);
        Assert.Equal(OperationState.Failed, snapshot.State);
        Assert.Equal(OperationErrorKind.FileIntegrity, snapshot.Error?.Kind);
    }

    [Fact]
    public async Task ResumesPartialDownloadWithRangeRequest()
    {
        using var environment = new TestEnvironment();
        using var provider = environment.CreateServices();
        var settings = provider.GetRequiredService<ISettingsService>();
        var handler = new StubHandler();
        var service = CreateService(settings, handler);

        var content = Encoding.UTF8.GetBytes("hello world");
        var hash = Convert.ToHexString(SHA1.HashData(content)).ToLowerInvariant();
        var destination = Path.Combine(environment.Root, "downloads", "resume.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await File.WriteAllTextAsync(destination + ".part", "hello ");

        handler.Respond = request =>
        {
            Assert.Equal("bytes=6-", request.Headers.Range?.ToString());
            return new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("world")),
            };
        };

        var request = new DownloadRequest(
            destination,
            [new Uri("https://example.com/resume.bin")],
            hash,
            HashingAlgorithm.Sha1,
            content.Length);

        await service.DownloadAsync([request]);
        Assert.Equal("hello world", await File.ReadAllTextAsync(destination));
    }

    [Fact]
    public async Task DownloadsTextWithHash()
    {
        using var environment = new TestEnvironment();
        using var provider = environment.CreateServices();
        var settings = provider.GetRequiredService<ISettingsService>();
        var handler = new StubHandler();
        var service = CreateService(settings, handler);

        var content = Encoding.UTF8.GetBytes("{\"ok\":true}");
        var hash = Convert.ToHexString(SHA1.HashData(content)).ToLowerInvariant();
        handler.Respond = _ => Ok(content);

        var text = await service.DownloadTextAsync(
            [new Uri("https://example.com/data.json")],
            hash,
            content.Length);

        Assert.Equal("{\"ok\":true}", text);
    }

    [Fact]
    public async Task FallsBackToNextUrlWhenConnectionTimesOut()
    {
        using var environment = new TestEnvironment();
        using var provider = environment.CreateServices();
        var settings = provider.GetRequiredService<ISettingsService>();
        var handler = new StubHandler();
        var service = CreateService(settings, handler);

        var content = Encoding.UTF8.GetBytes("fallback");
        var hash = Convert.ToHexString(SHA1.HashData(content)).ToLowerInvariant();
        handler.Respond = request =>
        {
            if (request.RequestUri!.Host == "slow.example.com")
            {
                // HttpClient 连接超时以 TaskCanceledException 抛出，但不是用户取消。
                throw new TaskCanceledException("connect timeout", new TimeoutException());
            }

            return Ok(content);
        };

        var destination = Path.Combine(environment.Root, "downloads", "fallback.bin");
        var request = new DownloadRequest(
            destination,
            [new Uri("https://slow.example.com/file.bin"), new Uri("https://fast.example.com/file.bin")],
            hash,
            HashingAlgorithm.Sha1,
            content.Length);

        var result = await service.DownloadAsync([request]);

        Assert.Equal(1, result.DownloadedFiles);
        Assert.True(handler.Calls >= 2);
        Assert.Equal("fallback", await File.ReadAllTextAsync(destination));
    }

    [Fact]
    public void PauseAndResumeTogglesControllerState()
    {
        using var environment = new TestEnvironment();
        using var provider = environment.CreateServices();
        var service = CreateService(provider.GetRequiredService<ISettingsService>(), new StubHandler());

        Assert.False(service.IsPaused);
        service.Pause();
        Assert.True(service.IsPaused);
        service.Resume();
        Assert.False(service.IsPaused);
    }

    [Fact]
    public async Task PublishesCompletedOperationWithBytesAndSource()
    {
        using var environment = new TestEnvironment();
        using var provider = environment.CreateServices();
        var handler = new StubHandler();
        var operations = new OperationService();
        var progress = new RecordingProgress();
        var service = CreateService(provider.GetRequiredService<ISettingsService>(), handler, operations);

        var content = Encoding.UTF8.GetBytes(new string('x', 200_000));
        var hash = Convert.ToHexString(SHA1.HashData(content)).ToLowerInvariant();
        handler.Respond = _ => Ok(content);

        var destination = Path.Combine(environment.Root, "downloads", "file.bin");
        var request = new DownloadRequest(
            destination,
            [new Uri("https://example.com/file.bin")],
            hash,
            HashingAlgorithm.Sha1,
            content.Length);

        await service.DownloadAsync([request], progress);

        var snapshot = Assert.Single(operations.Snapshots);
        Assert.Equal(OperationKind.Download, snapshot.Kind);
        Assert.Equal("file.bin", snapshot.Title);
        Assert.Equal("example.com", snapshot.Source);
        Assert.Equal(OperationState.Completed, snapshot.State);
        Assert.Equal(content.Length, snapshot.BytesDownloaded);
        Assert.Equal(content.Length, snapshot.TotalBytes);
        Assert.Equal(1, snapshot.Progress);

        var final = progress.Values[^1];
        Assert.Equal(content.Length, final.CurrentFileBytes);
        Assert.Equal(content.Length, final.CurrentFileTotalBytes);
        Assert.True(final.IsTotalBytesKnown);
        Assert.Equal(1, final.CompletedFiles);
    }

    [Fact]
    public async Task ReportsRetryingBeforeCompleting()
    {
        using var environment = new TestEnvironment();
        using var provider = environment.CreateServices();
        var handler = new StubHandler();
        var operations = new OperationService();
        var observed = new List<OperationSnapshot>();
        operations.Changed += (_, snapshot) =>
        {
            lock (observed)
            {
                observed.Add(snapshot);
            }
        };

        var service = CreateService(provider.GetRequiredService<ISettingsService>(), handler, operations);
        var content = Encoding.UTF8.GetBytes("retry me");
        var hash = Convert.ToHexString(SHA1.HashData(content)).ToLowerInvariant();
        var calls = 0;
        handler.Respond = _ => Interlocked.Increment(ref calls) == 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : Ok(content);

        var request = new DownloadRequest(
            Path.Combine(environment.Root, "downloads", "retry.bin"),
            [new Uri("https://example.com/retry.bin")],
            hash,
            HashingAlgorithm.Sha1,
            content.Length);

        await service.DownloadAsync([request]);

        OperationSnapshot[] snapshots;
        lock (observed)
        {
            snapshots = observed.ToArray();
        }

        Assert.Contains(snapshots, snapshot =>
            snapshot.State == OperationState.Retrying &&
            snapshot.RetryAttempt == 1 &&
            snapshot.RetryReason!.Contains("503", StringComparison.Ordinal));
        Assert.Equal(OperationState.Completed, Assert.Single(operations.Snapshots).State);
        Assert.True(calls >= 2);
    }

    [Fact]
    public async Task UnknownTotalSizeKeepsOperationIndeterminate()
    {
        using var environment = new TestEnvironment();
        using var provider = environment.CreateServices();
        var handler = new StubHandler();
        var operations = new OperationService();
        var progress = new RecordingProgress();
        var service = CreateService(provider.GetRequiredService<ISettingsService>(), handler, operations);
        handler.Respond = _ => Ok(Encoding.UTF8.GetBytes("payload"));

        var requests = new[]
        {
            new DownloadRequest(
                Path.Combine(environment.Root, "downloads", "one.bin"),
                [new Uri("https://example.com/one.bin")]),
            new DownloadRequest(
                Path.Combine(environment.Root, "downloads", "two.bin"),
                [new Uri("https://example.com/two.bin")]),
        };

        await service.DownloadAsync(requests, progress);

        var snapshot = Assert.Single(operations.Snapshots);
        Assert.Equal(OperationState.Completed, snapshot.State);
        Assert.Null(snapshot.TotalBytes);
        Assert.Null(snapshot.Progress);
        Assert.False(progress.Values[^1].IsTotalBytesKnown);
    }

    [Fact]
    public async Task CanceledDownloadBecomesCancelledOperation()
    {
        using var environment = new TestEnvironment();
        using var provider = environment.CreateServices();
        var handler = new StubHandler();
        var operations = new OperationService();
        var service = CreateService(provider.GetRequiredService<ISettingsService>(), handler, operations);
        handler.Respond = _ => Ok(Encoding.UTF8.GetBytes("never downloaded"));

        var request = new DownloadRequest(
            Path.Combine(environment.Root, "downloads", "canceled.bin"),
            [new Uri("https://example.com/canceled.bin")]);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.DownloadAsync([request], cancellationToken: cts.Token));

        var snapshot = Assert.Single(operations.Snapshots);
        Assert.Equal(OperationState.Cancelled, snapshot.State);
        Assert.Equal(OperationErrorKind.Cancelled, snapshot.Error?.Kind);
    }

    [Fact]
    public async Task EmptyBatchDoesNotCreateAnOperation()
    {
        using var environment = new TestEnvironment();
        using var provider = environment.CreateServices();
        var operations = new OperationService();
        var service = CreateService(provider.GetRequiredService<ISettingsService>(), new StubHandler(), operations);

        var result = await service.DownloadAsync([]);

        Assert.Equal(0, result.DownloadedFiles);
        Assert.Empty(operations.Snapshots);
    }

    [Fact]
    public async Task SkippedFilesAreCountedInTheOperation()
    {
        using var environment = new TestEnvironment();
        using var provider = environment.CreateServices();
        var handler = new StubHandler();
        var operations = new OperationService();
        var service = CreateService(provider.GetRequiredService<ISettingsService>(), handler, operations);
        var content = Encoding.UTF8.GetBytes("cached");
        var hash = Convert.ToHexString(SHA1.HashData(content)).ToLowerInvariant();
        handler.Respond = _ => Ok(content);
        var request = new DownloadRequest(
            Path.Combine(environment.Root, "downloads", "cached.bin"),
            [new Uri("https://example.com/cached.bin")],
            hash,
            HashingAlgorithm.Sha1,
            content.Length);

        await service.DownloadAsync([request]);
        await service.DownloadAsync([request]);

        var completed = operations.Snapshots
            .Where(snapshot => snapshot.State == OperationState.Completed)
            .OrderByDescending(snapshot => snapshot.StartedAt)
            .First();
        Assert.Equal(1, completed.Completed);
        Assert.Equal(content.Length, completed.BytesDownloaded);
    }

    [Fact]
    public async Task ServerIgnoringRangeRestartsFromScratch()
    {
        using var environment = new TestEnvironment();
        using var provider = environment.CreateServices();
        var handler = new StubHandler();
        var operations = new OperationService();
        var service = CreateService(provider.GetRequiredService<ISettingsService>(), handler, operations);

        var content = Encoding.UTF8.GetBytes("hello world");
        var hash = Convert.ToHexString(SHA1.HashData(content)).ToLowerInvariant();
        var destination = Path.Combine(environment.Root, "downloads", "range-ignored.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await File.WriteAllTextAsync(destination + ".part", "hello ");

        handler.Respond = request =>
        {
            Assert.Equal("bytes=6-", request.Headers.Range?.ToString());
            // 服务器忽略 Range，返回 200 + 完整内容；客户端必须从头覆盖 part 文件。
            return Ok(content);
        };

        var request = new DownloadRequest(
            destination,
            [new Uri("https://example.com/range-ignored.bin")],
            hash,
            HashingAlgorithm.Sha1,
            content.Length);

        await service.DownloadAsync([request]);

        Assert.Equal("hello world", await File.ReadAllTextAsync(destination));
        Assert.Equal(OperationState.Completed, Assert.Single(operations.Snapshots).State);
    }

    [Fact]
    public async Task SizeMismatchFailsOperationWithIntegrityReason()
    {
        using var environment = new TestEnvironment();
        using var provider = environment.CreateServices();
        var handler = new StubHandler();
        var operations = new OperationService();
        var service = CreateService(provider.GetRequiredService<ISettingsService>(), handler, operations);
        handler.Respond = _ => Ok(Encoding.UTF8.GetBytes("abc"));

        var request = new DownloadRequest(
            Path.Combine(environment.Root, "downloads", "truncated.bin"),
            [new Uri("https://example.com/truncated.bin")],
            Size: 10);

        await Assert.ThrowsAsync<DownloadException>(() => service.DownloadAsync([request]));

        var snapshot = Assert.Single(operations.Snapshots);
        Assert.Equal(OperationState.Failed, snapshot.State);
        Assert.Equal(OperationErrorKind.FileIntegrity, snapshot.Error?.Kind);
    }

    [Fact]
    public async Task PausedDownloadReportsPausedThenCompletesAfterResume()
    {
        using var environment = new TestEnvironment();
        using var provider = environment.CreateServices();
        var handler = new StubHandler();
        var operations = new OperationService();
        var progress = new RecordingProgress();
        var service = CreateService(provider.GetRequiredService<ISettingsService>(), handler, operations);
        var content = Encoding.UTF8.GetBytes("paused payload");
        var hash = Convert.ToHexString(SHA1.HashData(content)).ToLowerInvariant();
        handler.Respond = _ => Ok(content);

        var request = new DownloadRequest(
            Path.Combine(environment.Root, "downloads", "paused.bin"),
            [new Uri("https://example.com/paused.bin")],
            hash,
            HashingAlgorithm.Sha1,
            content.Length);

        service.Pause();
        var download = service.DownloadAsync([request], progress);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!operations.Snapshots.Any(snapshot => snapshot.IsPaused) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.Contains(operations.Snapshots, snapshot => snapshot.IsPaused);
        service.Resume();
        var result = await download;

        Assert.Equal(1, result.DownloadedFiles);
        Assert.Contains(progress.Values, value => value.IsPaused);
        Assert.Equal(OperationState.Completed, Assert.Single(operations.Snapshots).State);
    }

    [Fact]
    public async Task DependencyInjectionWiresTheOperationStoreIntoDownloads()
    {
        using var environment = new TestEnvironment();
        var handler = new StubHandler();
        using var provider = environment.CreateServices(services =>
            services.AddSingleton<IHttpClientProvider>(new StubHttpClientProvider(handler)));
        var store = provider.GetRequiredService<IOperationStore>();
        var downloads = provider.GetRequiredService<IDownloadService>();
        var content = Encoding.UTF8.GetBytes("wired");
        handler.Respond = _ => Ok(content);

        var request = new DownloadRequest(
            Path.Combine(environment.Root, "downloads", "wired.bin"),
            [new Uri("https://example.com/wired.bin")],
            Size: content.Length);

        await downloads.DownloadAsync([request]);

        var snapshot = Assert.Single(store.Snapshots);
        Assert.Equal(OperationKind.Download, snapshot.Kind);
        Assert.Equal(OperationState.Completed, snapshot.State);
    }

    private static DownloadService CreateService(
        ISettingsService settings,
        StubHandler handler,
        IOperationService? operations = null) =>
        new(new StubHttpClientProvider(handler), settings, NullLogger<DownloadService>.Instance, operations);

    private sealed class RecordingProgress : IProgress<DownloadProgress>
    {
        private readonly object _gate = new();
        private readonly List<DownloadProgress> _values = [];

        public IReadOnlyList<DownloadProgress> Values
        {
            get
            {
                lock (_gate)
                {
                    return _values.ToArray();
                }
            }
        }

        public void Report(DownloadProgress value)
        {
            lock (_gate)
            {
                _values.Add(value);
            }
        }
    }

    private static HttpResponseMessage Ok(byte[] content) =>
        new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(content),
        };

    private sealed class StubHttpClientProvider(HttpMessageHandler handler) : IHttpClientProvider
    {
        public HttpClient CreateClient() => new(handler, disposeHandler: false)
        {
            Timeout = TimeSpan.FromSeconds(5),
        };
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Respond(request));
        }
    }
}
