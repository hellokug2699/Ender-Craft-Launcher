// SPDX-License-Identifier: GPL-3.0-or-later

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Ecl.Core.Storage;

public enum InstanceSourceStructure
{
    Unknown,
    EclManaged,
    Versioned,
    SingleInstance,
}

public sealed record DiscoveredInstance
{
    public required string SourceId { get; init; }

    /// <summary>来源稳定性 ID；同名实例在不同来源不会冲突。</summary>
    public required string StableId { get; init; }

    public required string Name { get; init; }

    public required string FolderName { get; init; }

    public string? GameVersion { get; init; }

    public string Loader { get; init; } = "unknown";

    public required string GameDirectory { get; init; }

    public string? VersionId { get; init; }

    public bool IsManaged { get; init; }

    public bool CanLaunch => IsManaged;

    public string? Problem { get; init; }

    public string? DuplicateOf { get; init; }

    public DateTimeOffset? LastModifiedUtc { get; init; }
}

public sealed record InstanceSourceScanResult(
    string SourceId,
    string SourceName,
    string SourcePath,
    bool IsAccessible,
    InstanceSourceStructure Structure,
    IReadOnlyList<DiscoveredInstance> Instances,
    string? Error,
    DateTimeOffset ScannedAtUtc);

public sealed record InstanceSourceGroup(
    WorkspaceRoot Source,
    bool IsAccessible,
    InstanceSourceStructure Structure,
    IReadOnlyList<DiscoveredInstance> Instances,
    string? Error,
    DateTimeOffset? ScannedAtUtc)
{
    public long? TotalBytes { get; init; }
}

public sealed record InstanceCatalog(
    IReadOnlyList<InstanceSourceGroup> Groups,
    IReadOnlyList<string> Warnings)
{
    public int InstanceCount => Groups.Sum(group => group.Instances.Count);

    public static InstanceCatalog Empty => new([], []);
}

/// <summary>
/// 多实例来源扫描。只读取来源元数据，不复制、移动、改写或清理实例文件；
/// 每个来源独立失败，未知结构保持只读且不自动导入。
/// </summary>
public sealed class InstanceSourceService(IWorkspaceService workspace, ILogger<InstanceSourceService> logger)
{
    private static readonly string[] IsolationMarkers =
    [
        "mods",
        "config",
        "saves",
        "options.txt",
        "servers.dat",
        "resourcepacks",
        "shaderpacks",
    ];

    public async Task<InstanceCatalog> ScanAllAsync(
        CancellationToken cancellationToken = default,
        IProgress<InstanceSourceScanResult>? progress = null)
    {
        var roots = workspace.Roots;
        var duplicates = roots
            .GroupBy(root => Normalize(root.Path), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group.Skip(1))
            .Select(root => root.Id)
            .ToHashSet(StringComparer.Ordinal);
        var groups = new List<InstanceSourceGroup>();
        var warnings = new List<string>();
        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!root.Enabled)
            {
                continue;
            }

            InstanceSourceScanResult result;
            if (duplicates.Contains(root.Id))
            {
                result = new InstanceSourceScanResult(
                    root.Id,
                    root.Name,
                    root.Path,
                    false,
                    InstanceSourceStructure.Unknown,
                    [],
                    "该来源与另一个登记来源指向同一目录，已跳过重复扫描。",
                    DateTimeOffset.UtcNow);
            }
            else
            {
                result = await ScanAsync(root, cancellationToken).ConfigureAwait(false);
            }

            progress?.Report(result);
            if (result.Error is not null)
            {
                warnings.Add($"{result.SourceName}：{result.Error}");
            }

            groups.Add(new InstanceSourceGroup(
                root,
                result.IsAccessible,
                result.Structure,
                result.Instances,
                result.Error,
                result.ScannedAtUtc)
            {
                TotalBytes = null,
            });
        }

        return new InstanceCatalog(groups, warnings);
    }

    public async Task<InstanceSourceScanResult> ScanAsync(
        WorkspaceRoot root,
        CancellationToken cancellationToken = default)
    {
        var scannedAt = DateTimeOffset.UtcNow;
        var path = Normalize(root.Path);
        if (!Directory.Exists(path))
        {
            await UpdateRootAsync(root with
            {
                Status = WorkspaceStatus.Missing,
                LastScanError = "来源目录不存在或当前不可访问。",
                LastScannedAtUtc = scannedAt,
            }).ConfigureAwait(false);
            return new InstanceSourceScanResult(
                root.Id,
                root.Name,
                root.Path,
                false,
                InstanceSourceStructure.Unknown,
                [],
                "来源目录不存在或当前不可访问。",
                scannedAt);
        }

        try
        {
            var structure = DetectStructure(path);
            var discovered = new List<DiscoveredInstance>();
            var seenPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            switch (structure)
            {
                case InstanceSourceStructure.Versioned:
                    ScanVersioned(root, path, discovered, seenPaths, cancellationToken);
                    break;
                case InstanceSourceStructure.EclManaged:
                    ScanEclManaged(root, path, discovered, seenPaths, cancellationToken);
                    break;
                case InstanceSourceStructure.SingleInstance:
                    AddCandidate(
                        root,
                        path,
                        path,
                        Path.GetFileName(path),
                        null,
                        "unknown",
                        false,
                        null,
                        discovered,
                        seenPaths);
                    break;
                default:
                    await UpdateRootAsync(root with
                    {
                        Status = WorkspaceStatus.Ready,
                        Structure = structure.ToString(),
                        LastScanError = null,
                        LastScannedAtUtc = scannedAt,
                        LastValidatedAtUtc = scannedAt,
                    }).ConfigureAwait(false);
                    return new InstanceSourceScanResult(
                        root.Id,
                        root.Name,
                        root.Path,
                        true,
                        structure,
                        [],
                        "未识别到受支持的实例结构；未自动导入或清理任何内容。",
                        scannedAt);
            }

            await UpdateRootAsync(root with
            {
                Status = WorkspaceStatus.Ready,
                Structure = structure.ToString(),
                LastScanError = null,
                LastScannedAtUtc = scannedAt,
                LastValidatedAtUtc = scannedAt,
            }).ConfigureAwait(false);
            return new InstanceSourceScanResult(root.Id, root.Name, root.Path, true, structure, discovered, null, scannedAt);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning(exception, "扫描实例来源失败：{Path}", root.Path);
            await UpdateRootAsync(root with
            {
                Status = WorkspaceStatus.Unknown,
                LastScanError = exception.Message,
                LastScannedAtUtc = scannedAt,
            }).ConfigureAwait(false);
            return new InstanceSourceScanResult(
                root.Id,
                root.Name,
                root.Path,
                false,
                InstanceSourceStructure.Unknown,
                [],
                $"扫描失败：{exception.Message}",
                scannedAt);
        }
    }

    private static void ScanVersioned(
        WorkspaceRoot root,
        string sourcePath,
        List<DiscoveredInstance> discovered,
        Dictionary<string, string> seenPaths,
        CancellationToken cancellationToken)
    {
        var versions = Path.Combine(sourcePath, "versions");
        foreach (var directory in Directory.EnumerateDirectories(versions).Take(512))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var versionId = Path.GetFileName(directory);
            var manifest = Path.Combine(directory, versionId + ".json");
            if (!File.Exists(manifest))
            {
                continue;
            }

            var metadata = ReadVersionManifest(manifest);
            var isolated = IsolationMarkers.Any(marker =>
                File.Exists(Path.Combine(directory, marker)) || Directory.Exists(Path.Combine(directory, marker)));
            var gameDirectory = isolated ? directory : sourcePath;
            AddCandidate(
                root,
                sourcePath,
                directory,
                versionId,
                metadata.GameVersion,
                metadata.Loader,
                false,
                metadata.Problem,
                discovered,
                seenPaths,
                versionId,
                metadata.LastModified);
        }
    }

    private static void ScanEclManaged(
        WorkspaceRoot root,
        string sourcePath,
        List<DiscoveredInstance> discovered,
        Dictionary<string, string> seenPaths,
        CancellationToken cancellationToken)
    {
        var instances = Path.Combine(sourcePath, "instances");
        foreach (var directory in Directory.EnumerateDirectories(instances).Take(512))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var metadataPath = Path.Combine(directory, "instance.json");
            if (!File.Exists(metadataPath))
            {
                continue;
            }

            try
            {
                using var stream = File.OpenRead(metadataPath);
                using var document = JsonDocument.Parse(stream);
                var element = document.RootElement;
                var id = element.TryGetProperty("id", out var idValue) ? idValue.GetString() : null;
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                var gameDirectory = element.TryGetProperty("gameDirectory", out var gameValue)
                    ? gameValue.GetString()
                    : null;
                gameDirectory = string.IsNullOrWhiteSpace(gameDirectory)
                    ? Path.Combine(directory, "minecraft")
                    : gameDirectory;
                AddCandidate(
                    root,
                    sourcePath,
                    directory,
                    element.TryGetProperty("name", out var name) ? name.GetString() ?? id : id,
                    element.TryGetProperty("gameVersion", out var version) ? version.GetString() : null,
                    element.TryGetProperty("loader", out var loader) ? loader.GetString() ?? "vanilla" : "vanilla",
                    true,
                    null,
                    discovered,
                    seenPaths,
                    id,
                    Directory.GetLastWriteTimeUtc(directory),
                    gameDirectory);
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
            {
                // 单个实例元数据损坏不阻断其他实例。
            }
        }
    }

    private static void AddCandidate(
        WorkspaceRoot root,
        string sourcePath,
        string directory,
        string name,
        string? gameVersion,
        string loader,
        bool isManaged,
        string? problem,
        List<DiscoveredInstance> discovered,
        Dictionary<string, string> seenPaths,
        string? versionId = null,
        DateTime? lastModified = null,
        string? gameDirectoryOverride = null)
    {
        var relative = Path.GetRelativePath(sourcePath, directory).Replace('\\', '/');
        var stableId = CreateStableId(root.Id, relative);
        var gameDirectory = gameDirectoryOverride ?? directory;
        string? duplicateOf = null;
        if (seenPaths.TryGetValue(Normalize(gameDirectory), out var first))
        {
            duplicateOf = first;
        }
        else
        {
            seenPaths[Normalize(gameDirectory)] = stableId;
        }

        discovered.Add(new DiscoveredInstance
        {
            SourceId = root.Id,
            StableId = stableId,
            Name = name,
            FolderName = Path.GetFileName(directory),
            GameVersion = gameVersion,
            Loader = loader,
            GameDirectory = gameDirectory,
            VersionId = versionId,
            IsManaged = isManaged,
            Problem = problem,
            DuplicateOf = duplicateOf,
            LastModifiedUtc = lastModified is { } time ? new DateTimeOffset(time.ToUniversalTime()) : null,
        });
    }

    private static (string? GameVersion, string Loader, string? Problem, DateTime? LastModified) ReadVersionManifest(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            var id = root.TryGetProperty("id", out var idValue) ? idValue.GetString() : null;
            var clientVersion = root.TryGetProperty("clientVersion", out var clientValue)
                ? clientValue.GetString()
                : null;
            var inheritsFrom = root.TryGetProperty("inheritsFrom", out var parentValue)
                ? parentValue.GetString()
                : null;
            var loader = DetectLoader(id);
            var gameVersion = clientVersion
                              ?? (loader == "vanilla" ? id : inheritsFrom)
                              ?? inheritsFrom;
            var problem = string.IsNullOrWhiteSpace(gameVersion) ? "无法从版本清单确定 Minecraft 版本。" : null;
            return (gameVersion, loader, problem, File.GetLastWriteTimeUtc(path));
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return (null, "unknown", $"版本清单读取失败：{exception.Message}", null);
        }
    }

    private static string DetectLoader(string? versionId)
    {
        if (string.IsNullOrWhiteSpace(versionId))
        {
            return "unknown";
        }

        if (versionId.StartsWith("fabric-loader-", StringComparison.OrdinalIgnoreCase))
        {
            return "fabric";
        }

        if (versionId.StartsWith("neoforge-", StringComparison.OrdinalIgnoreCase))
        {
            return "neoforge";
        }

        if (versionId.StartsWith("forge-", StringComparison.OrdinalIgnoreCase) ||
            versionId.Contains("forge", StringComparison.OrdinalIgnoreCase))
        {
            return "forge";
        }

        if (versionId.Contains("cleanroom", StringComparison.OrdinalIgnoreCase))
        {
            return "cleanroom";
        }

        return "vanilla";
    }

    private static InstanceSourceStructure DetectStructure(string path)
    {
        if (Directory.Exists(Path.Combine(path, "versions")) &&
            Directory.EnumerateDirectories(Path.Combine(path, "versions")).Any())
        {
            return InstanceSourceStructure.Versioned;
        }

        if (Directory.Exists(Path.Combine(path, "instances")) &&
            Directory.EnumerateDirectories(Path.Combine(path, "instances")).Any())
        {
            return InstanceSourceStructure.EclManaged;
        }

        if (IsolationMarkers.Any(marker =>
                File.Exists(Path.Combine(path, marker)) || Directory.Exists(Path.Combine(path, marker))))
        {
            return InstanceSourceStructure.SingleInstance;
        }

        return InstanceSourceStructure.Unknown;
    }

    private async Task UpdateRootAsync(WorkspaceRoot root) =>
        await workspace.UpdateRootAsync(root).ConfigureAwait(false);

    private static string CreateStableId(string sourceId, string relativePath)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{sourceId}|{relativePath.ToLowerInvariant()}"));
        return "ext-" + Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private static string Normalize(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }
}
