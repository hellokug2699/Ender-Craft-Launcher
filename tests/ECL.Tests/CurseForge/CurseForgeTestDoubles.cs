// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;
using Ecl.Download;
using Ecl.Minecraft.CurseForge;

namespace Ecl.Tests.CurseForge;

internal sealed class FakeCurseForgeClient : ICurseForgeClient
{
    public CurseForgeStatus Status { get; set; } = new(CurseForgeKeySource.Embedded);

    public Dictionary<int, CurseForgeMod> Mods { get; } = [];

    public Dictionary<int, CurseForgeFile> Files { get; } = [];

    public List<CurseForgeFile> ListedFiles { get; } = [];

    public CurseForgeSearchResult SearchResult { get; set; } = new([], 0, 0, 20);

    public string? DownloadUrlOverride { get; set; }

    public bool ReturnNullDownloadUrl { get; set; }

    public int SearchCalls { get; private set; }

    public int GetFilesCalls { get; private set; }

    public Task<Result<CurseForgeSearchResult>> SearchModsAsync(
        CurseForgeSearchQuery query,
        CancellationToken cancellationToken = default)
    {
        SearchCalls++;
        return Task.FromResult(Result<CurseForgeSearchResult>.Ok(SearchResult));
    }

    public Task<Result<CurseForgeMod>> GetModAsync(
        int modId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Mods.TryGetValue(modId, out var mod)
            ? Result<CurseForgeMod>.Ok(mod)
            : Result<CurseForgeMod>.Fail("curseforge.not_found", $"缺少测试项目 {modId}"));

    public Task<Result<CurseForgeMod?>> FindModBySlugAsync(
        string slug,
        CancellationToken cancellationToken = default)
    {
        var mod = Mods.Values.FirstOrDefault(item =>
            string.Equals(item.Slug, slug, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(Result<CurseForgeMod?>.Ok(mod));
    }

    public Task<Result<IReadOnlyList<CurseForgeFile>>> GetFilesAsync(
        IReadOnlyList<int> fileIds,
        CancellationToken cancellationToken = default)
    {
        GetFilesCalls++;
        var files = fileIds
            .Distinct()
            .Select(id => Files.GetValueOrDefault(id))
            .Where(file => file is not null)
            .Cast<CurseForgeFile>()
            .ToArray();
        return Task.FromResult(Result<IReadOnlyList<CurseForgeFile>>.Ok(files));
    }

    public Task<Result<CurseForgeFile>> GetFileAsync(
        int modId,
        int fileId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Files.TryGetValue(fileId, out var file)
            ? Result<CurseForgeFile>.Ok(file)
            : Result<CurseForgeFile>.Fail("curseforge.not_found", $"缺少测试文件 {fileId}"));

    public Task<Result<IReadOnlyList<CurseForgeFile>>> GetFilesForModAsync(
        int modId,
        string? gameVersion,
        CurseForgeModLoader? loader,
        int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var files = ListedFiles.Count > 0
            ? ListedFiles
            : Files.Values.Where(file => file.ModId == modId).ToList();
        return Task.FromResult(Result<IReadOnlyList<CurseForgeFile>>.Ok(files));
    }

    public Task<Result<string?>> GetDownloadUrlAsync(
        int modId,
        int fileId,
        CancellationToken cancellationToken = default)
    {
        if (ReturnNullDownloadUrl)
        {
            return Task.FromResult(Result<string?>.Ok(null));
        }

        return Task.FromResult(Result<string?>.Ok(
            DownloadUrlOverride ?? Files.GetValueOrDefault(fileId)?.DownloadUrl));
    }
}

internal sealed class RecordingDownloadService : IDownloadService
{
    public List<DownloadRequest> Requests { get; } = [];

    public bool Fail { get; set; }

    public Task<DownloadBatchResult> DownloadAsync(
        IReadOnlyList<DownloadRequest> requests,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (Fail)
        {
            throw new DownloadException("模拟下载失败。", ["demo.jar: test"]);
        }

        Requests.AddRange(requests);
        foreach (var request in requests)
        {
            var directory = Path.GetDirectoryName(request.DestinationPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(request.DestinationPath, "demo");
        }

        progress?.Report(new DownloadProgress(
            requests.Count,
            requests.Count,
            requests.Count * 4L,
            requests.Count * 4L,
            "demo.jar"));
        return Task.FromResult(new DownloadBatchResult(requests.Count, 0, requests.Count * 4L));
    }

    public Task<string> DownloadTextAsync(
        IReadOnlyList<Uri> urls,
        string? expectedHash = null,
        long? expectedSize = null,
        HashingAlgorithm algorithm = HashingAlgorithm.Sha1,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}
