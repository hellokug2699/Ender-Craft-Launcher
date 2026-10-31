// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;
using Ecl.Download;
using Ecl.Minecraft.CurseForge;
using Ecl.Minecraft.Modrinth;

namespace Ecl.Tests.Modrinth;

internal sealed class FakeModrinthClient : IModrinthClient
{
    public List<ModrinthSearchHit> SearchHits { get; } = [];

    public Dictionary<string, List<ModrinthVersion>> Versions { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, ModrinthVersion> VersionsById { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int SearchCalls { get; private set; }

    public int VersionCalls { get; private set; }

    public ModrinthSearchQuery? LastQuery { get; private set; }

    public Task<Result<ModrinthSearchResult>> SearchAsync(
        ModrinthSearchQuery query,
        CancellationToken cancellationToken = default)
    {
        SearchCalls++;
        LastQuery = query;
        var hits = SearchHits.Take(query.PageSize).ToArray();
        return Task.FromResult(Result<ModrinthSearchResult>.Ok(
            new ModrinthSearchResult(hits, query.Offset, query.PageSize, SearchHits.Count)));
    }

    public Task<Result<ModrinthProject>> GetProjectAsync(
        string projectIdOrSlug,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Result<ModrinthProject>.Ok(new ModrinthProject
        {
            Id = projectIdOrSlug,
            Slug = projectIdOrSlug,
            Title = projectIdOrSlug,
        }));

    public Task<Result<IReadOnlyList<ModrinthVersion>>> GetVersionsAsync(
        string projectIdOrSlug,
        string? gameVersion = null,
        string? loader = null,
        CancellationToken cancellationToken = default)
    {
        VersionCalls++;
        var versions = Versions.TryGetValue(projectIdOrSlug, out var list) ? list : [];
        var filtered = versions
            .Where(version => string.IsNullOrWhiteSpace(gameVersion) || version.SupportsGameVersion(gameVersion))
            .Where(version => string.IsNullOrWhiteSpace(loader) || version.SupportsLoader(loader))
            .ToArray();
        return Task.FromResult(Result<IReadOnlyList<ModrinthVersion>>.Ok(filtered));
    }

    public Task<Result<ModrinthVersion>> GetVersionAsync(
        string versionId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(VersionsById.TryGetValue(versionId, out var version)
            ? Result<ModrinthVersion>.Ok(version)
            : Result<ModrinthVersion>.Fail("modrinth.not_found", $"缺少测试版本 {versionId}"));
}

internal sealed class FakeCurseForgeService : ICurseForgeService
{
    public CurseForgeStatus Status { get; set; } = new(CurseForgeKeySource.Proxy);

    public CurseForgeSearchResult SearchResult { get; set; } = new([], 0, 0, 20);

    public int SearchCalls { get; private set; }

    public Task<Result<CurseForgeSearchResult>> SearchAsync(
        CurseForgeSearchQuery query,
        CancellationToken cancellationToken = default)
    {
        SearchCalls++;
        return Task.FromResult(Result<CurseForgeSearchResult>.Ok(SearchResult));
    }

    public Task<Result<CurseForgeInstallReport>> InstallAsync(
        string instanceId,
        string modReference,
        int? fileId = null,
        bool includeDependencies = true,
        IProgress<CurseForgeProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Result<CurseForgeInstallReport>.Fail(
            "curseforge.test",
            "测试替身不实现安装。"));
}

internal sealed class StubHttpClientProvider(HttpMessageHandler handler) : IHttpClientProvider
{
    public HttpClient CreateClient() => new(handler, disposeHandler: false)
    {
        BaseAddress = new Uri("https://api.modrinth.com/v2/"),
    };
}

internal sealed class StubHttpMessageHandler(Func<HttpRequestMessage, string> respond) : HttpMessageHandler
{
    public List<Uri> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(respond(request), System.Text.Encoding.UTF8, "application/json"),
        });
    }
}

/// <summary>写入固定字节的下载替身；用于把 .mrpack 真正落到磁盘再走导入流程。</summary>
internal sealed class PayloadDownloadService(byte[] payload) : IDownloadService
{
    public List<DownloadRequest> Requests { get; } = [];

    public Task<DownloadBatchResult> DownloadAsync(
        IReadOnlyList<DownloadRequest> requests,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Requests.AddRange(requests);
        foreach (var request in requests)
        {
            var directory = Path.GetDirectoryName(request.DestinationPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllBytes(request.DestinationPath, payload);
        }

        progress?.Report(new DownloadProgress(
            requests.Count,
            requests.Count,
            requests.Count * (long)payload.Length,
            requests.Count * (long)payload.Length,
            "payload"));
        return Task.FromResult(new DownloadBatchResult(requests.Count, 0, requests.Count * (long)payload.Length));
    }

    public Task<string> DownloadTextAsync(
        IReadOnlyList<Uri> urls,
        string? expectedHash = null,
        long? expectedSize = null,
        HashingAlgorithm algorithm = HashingAlgorithm.Sha1,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}
