// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using Ecl.Core;

namespace Ecl.VanillaToolkit;

/// <summary>
/// 版本适用性判断。版本无法精确比较时采用保守策略：只有显式列出的版本或完全相等的
/// introduced 才视为适用，绝不静默套用“最接近版本”的数据（ADR 0040 §4）。
/// </summary>
public static partial class MinecraftVersionRules
{
    public static bool Matches(RegistryEntry entry, string targetVersion)
    {
        if (entry.Versions.Count > 0)
        {
            if (!entry.Versions.Contains(targetVersion, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(entry.Introduced))
        {
            if (string.Equals(entry.Introduced, targetVersion, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!TryCompare(targetVersion, entry.Introduced!, out var introducedComparison) || introducedComparison < 0)
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(entry.Removed))
        {
            if (string.Equals(entry.Removed, targetVersion, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (TryCompare(targetVersion, entry.Removed!, out var removedComparison) && removedComparison >= 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>只比较正式版本 x.y 或 x.y.z；快照/预发布等无法可靠排序时返回 false。</summary>
    public static bool TryCompare(string? left, string? right, out int comparison)
    {
        comparison = 0;
        if (!TryParseRelease(left, out var leftParts) || !TryParseRelease(right, out var rightParts))
        {
            return false;
        }

        for (var index = 0; index < 3; index++)
        {
            comparison = leftParts[index].CompareTo(rightParts[index]);
            if (comparison != 0)
            {
                return true;
            }
        }

        return true;
    }

    private static bool TryParseRelease(string? value, out int[] parts)
    {
        parts = [0, 0, 0];
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var match = ReleaseVersionRegex().Match(value.Trim());
        if (!match.Success)
        {
            return false;
        }

        parts[0] = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        parts[1] = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        parts[2] = match.Groups[3].Success
            ? int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture)
            : 0;
        return true;
    }

    [GeneratedRegex(@"^(\d{1,3})\.(\d{1,3})(?:\.(\d{1,3}))?$")]
    private static partial Regex ReleaseVersionRegex();
}

public sealed record RegistryQueryResponse(
    string MinecraftVersion,
    string DataVersion,
    IReadOnlyList<RegistryQueryResult> Results,
    string? PreferredLanguage);

/// <summary>
/// 原版注册表查询（ADR 0040 §2）。结果为结构化、可复制条目；数据质量与来源随结果返回，
/// 不把未验证数据包装成确定事实。
/// </summary>
public sealed class RegistryQueryService
{
    private readonly LoadedVanillaDataPack _pack;
    private readonly string _minecraftVersion;

    public RegistryQueryService(LoadedVanillaDataPack pack, string minecraftVersion)
    {
        _pack = pack;
        _minecraftVersion = minecraftVersion;
    }

    public IReadOnlyList<string> SupportedVersions => _pack.Manifest.MinecraftVersions;

    public Result<RegistryQueryResponse> Query(
        string text,
        string? kind = null,
        string? preferredLanguage = null,
        int limit = 50)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Result<RegistryQueryResponse>.Fail("toolkit.query_empty", "查询内容不能为空。");
        }

        if (!_pack.Manifest.MinecraftVersions.Contains(_minecraftVersion, StringComparer.OrdinalIgnoreCase))
        {
            return Result<RegistryQueryResponse>.Fail(
                "toolkit.version_unsupported",
                $"数据包不支持 Minecraft {_minecraftVersion}；请选择数据包声明的版本，不会自动改用其他版本。",
                string.Join("、", _pack.Manifest.MinecraftVersions));
        }

        var query = text.Trim();
        var matches = new List<RegistryQueryResult>();
        foreach (var entry in _pack.Registry)
        {
            if (!string.IsNullOrWhiteSpace(kind) &&
                !string.Equals(entry.Kind, kind, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!MinecraftVersionRules.Matches(entry, _minecraftVersion))
            {
                continue;
            }

            var match = Match(entry, query);
            if (match is null)
            {
                continue;
            }

            matches.Add(new RegistryQueryResult(entry, match.Value, entry.Quality, entry.SourceId));
        }

        limit = Math.Clamp(limit, 1, 200);
        var ordered = matches
            .OrderBy(item => item.Match)
            .ThenBy(item => item.Entry.Id, StringComparer.Ordinal)
            .Take(limit)
            .ToArray();
        return Result<RegistryQueryResponse>.Ok(
            new RegistryQueryResponse(_minecraftVersion, _pack.Manifest.DataVersion, ordered, preferredLanguage));
    }

    public static string DisplayName(RegistryEntry entry, string? preferredLanguage = null)
    {
        if (!string.IsNullOrWhiteSpace(preferredLanguage) &&
            entry.Names.TryGetValue(preferredLanguage, out var preferred))
        {
            return preferred;
        }

        foreach (var language in new[] { "zh-CN", "zh-Hans", "en-US" })
        {
            if (entry.Names.TryGetValue(language, out var name))
            {
                return name;
            }
        }

        return entry.Names.Values.FirstOrDefault() ?? entry.Id;
    }

    private static RegistryMatchKind? Match(RegistryEntry entry, string query)
    {
        if (string.Equals(entry.Id, query, StringComparison.OrdinalIgnoreCase))
        {
            return RegistryMatchKind.ExactId;
        }

        if (entry.Names.Values.Any(name => string.Equals(name, query, StringComparison.OrdinalIgnoreCase)))
        {
            return RegistryMatchKind.ExactName;
        }

        if (entry.Aliases.Any(alias => string.Equals(alias, query, StringComparison.OrdinalIgnoreCase)))
        {
            return RegistryMatchKind.Alias;
        }

        if (entry.Id.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            entry.Names.Values.Any(name => name.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
            entry.Aliases.Any(alias => alias.Contains(query, StringComparison.OrdinalIgnoreCase)))
        {
            return RegistryMatchKind.Partial;
        }

        return null;
    }
}

/// <summary>打开数据包并创建指定 Minecraft 版本的查询服务。</summary>
public static class VanillaToolkitFacade
{
    public static async Task<Result<RegistryQueryService>> OpenRegistryAsync(
        string packDirectory,
        string minecraftVersion,
        CancellationToken cancellationToken = default)
    {
        var loaded = await VanillaDataPackLoader.LoadAsync(packDirectory, cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<RegistryQueryService>.Fail(loaded.Error!);
        }

        if (!loaded.Value!.Manifest.MinecraftVersions.Contains(minecraftVersion, StringComparer.OrdinalIgnoreCase))
        {
            return Result<RegistryQueryService>.Fail(
                "toolkit.version_unsupported",
                $"数据包不支持 Minecraft {minecraftVersion}；请选择数据包声明的版本，不会自动改用其他版本。",
                string.Join("、", loaded.Value.Manifest.MinecraftVersions));
        }

        return Result<RegistryQueryService>.Ok(new RegistryQueryService(loaded.Value, minecraftVersion));
    }
}

/// <summary>
/// 本地实例资源解析（ADR 0040 §6）：只读取与目标版本精确匹配的实例原始客户端 jar，
/// 不内置纹理、不从网络补充、不跨版本借用。资源包覆盖暂未实现，调用方必须如实显示来源。
/// </summary>
public sealed class InstalledVersionResources
{
    public const int MaxTextureBytes = 8 * 1024 * 1024;

    private readonly string _versionDirectory;
    private readonly string _jarPath;

    private InstalledVersionResources(string versionDirectory, string jarPath, string minecraftVersion)
    {
        _versionDirectory = versionDirectory;
        _jarPath = jarPath;
        MinecraftVersion = minecraftVersion;
    }

    public string MinecraftVersion { get; }

    public static Result<InstalledVersionResources> Create(string? versionDirectory, string targetVersion)
    {
        if (string.IsNullOrWhiteSpace(versionDirectory) || !Directory.Exists(versionDirectory))
        {
            return Result<InstalledVersionResources>.Fail(
                "toolkit.resources_unavailable",
                "没有可用的版本实例；请安装或选择对应版本的实例。");
        }

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(versionDirectory));
        if (!string.Equals(Path.GetFileName(full), targetVersion, StringComparison.OrdinalIgnoreCase))
        {
            return Result<InstalledVersionResources>.Fail(
                "toolkit.resources_version_mismatch",
                $"资源实例版本（{Path.GetFileName(full)}）与查询版本（{targetVersion}）不一致。");
        }

        var jar = Path.Combine(full, $"{targetVersion}.jar");
        if (!File.Exists(jar))
        {
            return Result<InstalledVersionResources>.Fail(
                "toolkit.resources_unavailable",
                "实例缺少客户端 jar，无法解析本地资源。");
        }

        return Result<InstalledVersionResources>.Ok(new InstalledVersionResources(full, jar, targetVersion));
    }

    /// <summary>读取 <c>assets/minecraft/textures/</c> 下的资源；路径越界或缺失时明确失败。</summary>
    public Result<byte[]> ReadTexture(string resourcePath)
    {
        if (string.IsNullOrWhiteSpace(resourcePath) ||
            resourcePath.Contains("..", StringComparison.Ordinal) ||
            Path.IsPathRooted(resourcePath))
        {
            return Result<byte[]>.Fail("toolkit.resource_path_invalid", "资源路径无效。");
        }

        var entryPath = $"assets/minecraft/textures/{resourcePath.TrimStart('/')}";
        try
        {
            using var archive = ZipFile.OpenRead(_jarPath);
            var entry = archive.GetEntry(entryPath);
            if (entry is null || entry.Length <= 0 || entry.Length > MaxTextureBytes)
            {
                return Result<byte[]>.Fail(
                    "toolkit.resource_missing",
                    $"本地实例中没有资源 {resourcePath}。");
            }

            using var stream = entry.Open();
            using var buffer = new MemoryStream((int)entry.Length);
            stream.CopyTo(buffer);
            return Result<byte[]>.Ok(buffer.ToArray());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return Result<byte[]>.Fail("toolkit.resource_unreadable", "读取本地资源失败。", exception.Message);
        }
    }
}
