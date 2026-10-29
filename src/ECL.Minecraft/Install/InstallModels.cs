// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;

namespace Ecl.Minecraft.Install;

public enum InstallStage
{
    Manifest,
    Version,
    Loader,
    Processors,
    Client,
    Libraries,
    Assets,
    Logging,
    FabricApi,
    OptiFine,
    Completed,
}

public sealed record InstallProgress(
    InstallStage Stage,
    string Message,
    int CompletedFiles,
    int TotalFiles,
    long CompletedBytes,
    long TotalBytes,
    string? CurrentFile)
{
    public bool IsIndeterminate => TotalFiles == 0;

    public double Percent => TotalFiles > 0
        ? Math.Clamp(CompletedFiles * 100.0 / TotalFiles, 0, 100)
        : 0;

    public double BytesPerSecond { get; init; }

    public bool IsPaused { get; init; }
}

public sealed record InstallReport(
    string VersionId,
    int DownloadedFiles,
    int SkippedFiles,
    long Bytes,
    TimeSpan Duration,
    IReadOnlyList<string>? Components = null);

public interface IVersionInstallService
{
    Task<Result<InstallReport>> InstallAsync(
        string instanceId,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
