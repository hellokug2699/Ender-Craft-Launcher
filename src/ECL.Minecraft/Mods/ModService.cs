// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;
using Ecl.Minecraft.Instances;
using Microsoft.Extensions.Logging;

namespace Ecl.Minecraft.Mods;

public sealed class ModService(
    IInstanceService instances,
    IAppPaths paths,
    ILogger<ModService> logger) : IModService
{
    private static readonly HashSet<string> PlatformDependencies = new(StringComparer.OrdinalIgnoreCase)
    {
        "minecraft",
        "java",
        "fabricloader",
        "fabric-loader",
        "fabric",
        "forge",
        "neoforge",
        "fml",
        "cleanroom",
        "optifine",
        "javafml",
        "lowcodefml",
    };

    public async Task<Result<ModScanReport>> ScanAsync(
        string instanceId,
        CancellationToken cancellationToken = default)
    {
        var instance = await instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return Result<ModScanReport>.Fail("instance.not_found", "实例不存在。");
        }

        var modsDirectory = GetModsDirectory(instance);
        Directory.CreateDirectory(modsDirectory);

        var entries = new List<ModEntry>();
        foreach (var file in Directory
                     .EnumerateFiles(modsDirectory, "*", SearchOption.TopDirectoryOnly)
                     .Where(IsModFile)
                     .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(file);
            var enabled = IsEnabledFile(fileName);
            var result = ModMetadataReader.Read(file);
            entries.Add(new ModEntry(
                fileName,
                Path.GetFullPath(file),
                enabled,
                result.Metadata,
                result.Error,
                []));
        }

        var issues = Analyze(instance, entries);
        var reportEntries = entries
            .Select(entry => entry with
            {
                Issues = issues
                    .Where(issue => string.Equals(issue.FileName, entry.FileName, StringComparison.OrdinalIgnoreCase))
                    .ToArray(),
            })
            .ToArray();

        return Result<ModScanReport>.Ok(new ModScanReport(instance.Id, modsDirectory, reportEntries, issues));
    }

    public async Task<Result> SetEnabledAsync(
        string instanceId,
        string fileName,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        var resolved = await ResolveModFileAsync(instanceId, fileName, cancellationToken).ConfigureAwait(false);
        if (!resolved.IsSuccess)
        {
            return Result.Fail(resolved.Error!);
        }

        return SetEnabledCore(resolved.Value!.Directory, resolved.Value.FileName, enabled);
    }

    public async Task<Result<ModBatchResult>> SetAllEnabledAsync(
        string instanceId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        var instance = await instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return Result<ModBatchResult>.Fail("instance.not_found", "实例不存在。");
        }

        var modsDirectory = GetModsDirectory(instance);
        Directory.CreateDirectory(modsDirectory);

        var succeeded = 0;
        var errors = new List<string>();
        foreach (var file in Directory
                     .EnumerateFiles(modsDirectory, "*", SearchOption.TopDirectoryOnly)
                     .Where(IsModFile)
                     .ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = SetEnabledCore(modsDirectory, Path.GetFileName(file), enabled);
            if (result.IsSuccess)
            {
                succeeded++;
            }
            else
            {
                errors.Add($"{Path.GetFileName(file)}: {result.Error?.Message}");
            }
        }

        return Result<ModBatchResult>.Ok(new ModBatchResult(succeeded, errors));
    }

    public async Task<Result<ModBatchResult>> ImportAsync(
        string instanceId,
        IReadOnlyList<string> sourcePaths,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        var instance = await instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return Result<ModBatchResult>.Fail("instance.not_found", "实例不存在。");
        }

        var modsDirectory = GetModsDirectory(instance);
        Directory.CreateDirectory(modsDirectory);

        var succeeded = 0;
        var errors = new List<string>();
        foreach (var source in sourcePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(source))
            {
                errors.Add($"{source}: 文件不存在。");
                continue;
            }

            var fileName = Path.GetFileName(source);
            if (!IsModFile(fileName))
            {
                errors.Add($"{fileName}: 不是 .jar 或 .jar.disabled 文件。");
                continue;
            }

            var destination = Path.Combine(modsDirectory, fileName);
            if (File.Exists(destination) && !overwrite)
            {
                errors.Add($"{fileName}: 目标已存在。");
                continue;
            }

            try
            {
                File.Copy(source, destination, overwrite);
                succeeded++;
                logger.LogInformation("已导入 Mod {File} 到实例 {InstanceId}。", fileName, instance.Id);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{fileName}: {exception.Message}");
            }
        }

        return Result<ModBatchResult>.Ok(new ModBatchResult(succeeded, errors));
    }

    public async Task<Result> DeleteAsync(
        string instanceId,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        var resolved = await ResolveModFileAsync(instanceId, fileName, cancellationToken).ConfigureAwait(false);
        if (!resolved.IsSuccess)
        {
            return Result.Fail(resolved.Error!);
        }

        try
        {
            File.Delete(Path.Combine(resolved.Value!.Directory, resolved.Value.FileName));
            return Result.Ok();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result.Fail("mod.delete_failed", $"删除 Mod 失败：{exception.Message}");
        }
    }

    private static Result SetEnabledCore(string modsDirectory, string fileName, bool enabled)
    {
        var source = Path.Combine(modsDirectory, fileName);
        if (!File.Exists(source))
        {
            return Result.Fail("mod.not_found", $"Mod 不存在：{fileName}");
        }

        var isEnabled = IsEnabledFile(fileName);
        var isDisabled = IsDisabledFile(fileName);
        if (!isEnabled && !isDisabled)
        {
            return Result.Fail("mod.invalid_file", $"不是有效的 Mod 文件名：{fileName}");
        }

        if (enabled == isEnabled)
        {
            return Result.Ok();
        }

        var targetName = enabled
            ? fileName[..^".disabled".Length]
            : fileName + ".disabled";
        var target = Path.Combine(modsDirectory, targetName);
        if (File.Exists(target))
        {
            return Result.Fail("mod.target_exists", $"目标文件已存在：{targetName}");
        }

        try
        {
            File.Move(source, target);
            return Result.Ok();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result.Fail("mod.rename_failed", $"修改 Mod 状态失败：{exception.Message}");
        }
    }

    private async Task<Result<ResolvedModFile>> ResolveModFileAsync(
        string instanceId,
        string fileName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(fileName) ||
            !string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal))
        {
            return Result<ResolvedModFile>.Fail("mod.invalid_file", "Mod 文件名不合法。");
        }

        var instance = await instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return Result<ResolvedModFile>.Fail("instance.not_found", "实例不存在。");
        }

        var modsDirectory = GetModsDirectory(instance);
        var actualFile = Directory
            .EnumerateFiles(modsDirectory, "*", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .FirstOrDefault(candidate => string.Equals(candidate, fileName, StringComparison.OrdinalIgnoreCase));
        if (actualFile is null)
        {
            return Result<ResolvedModFile>.Fail("mod.not_found", $"Mod 不存在：{fileName}");
        }

        return Result<ResolvedModFile>.Ok(new ResolvedModFile(modsDirectory, actualFile));
    }

    private static IReadOnlyList<ModIssue> Analyze(GameInstance instance, IReadOnlyList<ModEntry> entries)
    {
        var issues = new List<ModIssue>();
        var withMetadata = entries.Where(entry => entry.Metadata is not null).ToArray();

        var byId = withMetadata
            .GroupBy(entry => entry.Metadata!.ModId, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .ToArray();

        foreach (var group in byId)
        {
            var files = group.Select(entry => entry.FileName).ToArray();
            foreach (var entry in group)
            {
                issues.Add(new ModIssue(
                    ModIssueKind.DuplicateModId,
                    entry.FileName,
                    group.Key,
                    RelatedModId: string.Join(", ", files.Where(file => file != entry.FileName))));
            }
        }

        var enabled = withMetadata.Where(entry => entry.Enabled).ToArray();
        var enabledIds = enabled
            .Select(entry => entry.Metadata!.ModId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var disabledIds = withMetadata
            .Where(entry => !entry.Enabled)
            .Select(entry => entry.Metadata!.ModId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in enabled)
        {
            foreach (var dependency in entry.Metadata!.Dependencies.Where(item => item.Mandatory))
            {
                if (PlatformDependencies.Contains(dependency.ModId))
                {
                    continue;
                }

                if (enabledIds.Contains(dependency.ModId))
                {
                    continue;
                }

                issues.Add(new ModIssue(
                    disabledIds.Contains(dependency.ModId)
                        ? ModIssueKind.DisabledDependency
                        : ModIssueKind.MissingDependency,
                    entry.FileName,
                    entry.Metadata.ModId,
                    dependency.ModId,
                    dependency.VersionRange));
            }

            foreach (var conflict in entry.Metadata.Breaks)
            {
                if (enabledIds.Contains(conflict))
                {
                    issues.Add(new ModIssue(
                        ModIssueKind.Conflict,
                        entry.FileName,
                        entry.Metadata.ModId,
                        conflict));
                }
            }
        }

        foreach (var entry in entries)
        {
            if (entry.Metadata is not null && !IsPlatformCompatible(instance.Loader, entry.Metadata.Platform))
            {
                issues.Add(new ModIssue(
                    ModIssueKind.LoaderMismatch,
                    entry.FileName,
                    entry.Metadata.ModId,
                    Detail: entry.Metadata.Platform.ToString()));
            }

            if (!string.IsNullOrWhiteSpace(entry.Error))
            {
                issues.Add(new ModIssue(
                    ModIssueKind.InvalidMetadata,
                    entry.FileName,
                    Detail: entry.Error));
            }
        }

        return issues;
    }

    private static bool IsPlatformCompatible(string loader, ModPlatform platform)
    {
        if (platform is ModPlatform.Unknown)
        {
            return true;
        }

        return loader.ToLowerInvariant() switch
        {
            "fabric" => platform == ModPlatform.Fabric,
            "forge" => platform is ModPlatform.Forge or ModPlatform.LegacyForge,
            "neoforge" => platform == ModPlatform.NeoForge,
            "cleanroom" => platform is ModPlatform.Forge or ModPlatform.LegacyForge,
            "vanilla" => false,
            _ => true,
        };
    }

    private string GetModsDirectory(GameInstance instance) =>
        Path.Combine(paths.GetInstanceGameDirectory(instance.Id), "mods");

    private static bool IsModFile(string path) =>
        IsEnabledFile(Path.GetFileName(path)) || IsDisabledFile(Path.GetFileName(path));

    private static bool IsEnabledFile(string fileName) =>
        fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase);

    private static bool IsDisabledFile(string fileName) =>
        fileName.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase);

    private sealed record ResolvedModFile(string Directory, string FileName);
}
