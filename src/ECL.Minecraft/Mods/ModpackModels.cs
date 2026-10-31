// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;

namespace Ecl.Minecraft.Mods;

public sealed record ModpackProgress(
    string Stage,
    string Message,
    int Completed,
    int Total,
    string? CurrentFile)
{
    /// <summary>当前阶段已处理的字节数；下载阶段由 <c>DownloadProgress</c> 提供。</summary>
    public long CompletedBytes { get; init; }

    /// <summary>当前阶段的已知总字节数；未知时为 0。</summary>
    public long TotalBytes { get; init; }

    public double BytesPerSecond { get; init; }
}

public sealed record ModpackImportReport(
    string Name,
    string? Version,
    int DownloadedFiles,
    int SkippedFiles,
    int OverrideFiles,
    IReadOnlyList<string> Warnings);

public sealed record ModpackExportReport(
    string OutputPath,
    int Files,
    bool ContainsDownloads);

public interface IModpackService
{
    Task<Result<ModpackImportReport>> ImportAsync(
        string instanceId,
        string archivePath,
        IProgress<ModpackProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<Result<ModpackExportReport>> ExportAsync(
        string instanceId,
        string outputPath,
        CancellationToken cancellationToken = default);
}
