// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using System.Security.Cryptography;
using System.Text;
using Ecl.Configuration.Settings;
using Ecl.Download;
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
        var service = CreateService(settings, handler);

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

    private static DownloadService CreateService(ISettingsService settings, StubHandler handler) =>
        new(new StubHttpClientProvider(handler), settings, NullLogger<DownloadService>.Instance);

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
