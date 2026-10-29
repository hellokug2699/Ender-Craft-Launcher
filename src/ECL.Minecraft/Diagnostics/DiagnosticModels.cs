// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;

namespace Ecl.Minecraft.Diagnostics;

public sealed record CrashSuspect(
    string ModId,
    string? Name,
    int Score,
    IReadOnlyList<string> Evidence);

public sealed record CrashAnalysis(
    string? SourcePath,
    string Summary,
    string Description,
    string? MinecraftVersion,
    string? JavaVersion,
    IReadOnlyList<CrashSuspect> Suspects,
    IReadOnlyList<string> KeyLines);

public sealed record DiagnosticPackageReport(string OutputPath, int Entries, long Bytes);

public interface ICrashReportService
{
    Task<Result<CrashAnalysis?>> AnalyzeLatestAsync(
        string instanceId,
        CancellationToken cancellationToken = default);
}

public interface IDiagnosticPackageService
{
    Task<Result<DiagnosticPackageReport>> ExportAsync(
        string instanceId,
        string? outputPath = null,
        CancellationToken cancellationToken = default);
}
