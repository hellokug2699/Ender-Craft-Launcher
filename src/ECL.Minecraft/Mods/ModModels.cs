// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;

namespace Ecl.Minecraft.Mods;

public enum ModIssueKind
{
    DuplicateModId,
    MissingDependency,
    DisabledDependency,
    Conflict,
    LoaderMismatch,
    InvalidMetadata,
}

public sealed record ModIssue(
    ModIssueKind Kind,
    string FileName,
    string? ModId = null,
    string? RelatedModId = null,
    string? Detail = null);

public sealed record ModEntry(
    string FileName,
    string FullPath,
    bool Enabled,
    ModMetadata? Metadata,
    string? Error,
    IReadOnlyList<ModIssue> Issues);

public sealed record ModScanReport(
    string InstanceId,
    string ModsDirectory,
    IReadOnlyList<ModEntry> Mods,
    IReadOnlyList<ModIssue> Issues)
{
    public int EnabledCount => Mods.Count(mod => mod.Enabled);

    public int DisabledCount => Mods.Count(mod => !mod.Enabled);

    public int IssueCount => Issues.Count;
}

public sealed record ModBatchResult(int Succeeded, IReadOnlyList<string> Errors);

public interface IModService
{
    Task<Result<ModScanReport>> ScanAsync(string instanceId, CancellationToken cancellationToken = default);

    Task<Result> SetEnabledAsync(
        string instanceId,
        string fileName,
        bool enabled,
        CancellationToken cancellationToken = default);

    Task<Result<ModBatchResult>> SetAllEnabledAsync(
        string instanceId,
        bool enabled,
        CancellationToken cancellationToken = default);

    Task<Result<ModBatchResult>> ImportAsync(
        string instanceId,
        IReadOnlyList<string> sourcePaths,
        bool overwrite = false,
        CancellationToken cancellationToken = default);

    Task<Result> DeleteAsync(
        string instanceId,
        string fileName,
        CancellationToken cancellationToken = default);
}
