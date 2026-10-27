// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using System.Text.RegularExpressions;
using Ecl.Minecraft.Loaders;
using Ecl.Minecraft.Versions;

namespace Ecl.Minecraft.Install;

/// <summary>
/// 把 install_profile 的 processors 翻译成「主类 + 类路径 + 参数」。
/// 纯函数实现，便于单元测试；真正的进程启动在 <see cref="ForgeProcessorRunner"/>。
/// </summary>
public static partial class ForgeProcessorPlanner
{
    public const string ClientSide = "client";

    public static IReadOnlyList<ForgeProcessor> SelectProcessors(
        ForgeInstallProfile profile,
        string side) =>
        profile.Processors
            .Where(processor =>
                processor.Sides is null ||
                processor.Sides.Count == 0 ||
                processor.Sides.Any(item => string.Equals(item, side, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

    /// <summary>
    /// spec 0 的旧安装器没有 processors，不需要 Java；只有真正要执行 processor 时才要求 Java。
    /// </summary>
    public static bool HasClientProcessors(ForgeInstallProfile profile) =>
        SelectProcessors(profile, ClientSide).Count > 0;

    public static IReadOnlyDictionary<string, string> BuildTokens(
        ForgeInstallProfile profile,
        string side,
        string installerPath,
        string root,
        string minecraftJar,
        string gameVersion,
        string librariesDirectory,
        Func<string, string> extractInstallerEntry)
    {
        var tokens = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["INSTALLER"] = installerPath,
            ["ROOT"] = root,
            ["MINECRAFT_JAR"] = minecraftJar,
            ["MINECRAFT_VERSION"] = profile.Minecraft ?? gameVersion,
            ["SIDE"] = side,
            ["LIBRARY_DIR"] = librariesDirectory,
        };

        if (profile.Data is null)
        {
            return tokens;
        }

        foreach (var (key, element) in profile.Data)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            var raw = ExtractSideValue(element, side);
            if (raw is null)
            {
                continue;
            }

            tokens[key] = ResolveDataValue(raw, librariesDirectory, extractInstallerEntry);
        }

        return tokens;
    }

    public static string? ExtractSideValue(JsonElement element, string side)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            return element.GetString();
        }

        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(side, out var value) &&
            value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return null;
    }

    public static string ResolveDataValue(
        string raw,
        string librariesDirectory,
        Func<string, string> extractInstallerEntry)
    {
        var value = raw.Trim();

        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
        {
            return value[1..^1];
        }

        if (value.StartsWith('/') || value.StartsWith('\\'))
        {
            var entryName = value.TrimStart('/', '\\').Replace('\\', '/');
            return extractInstallerEntry(entryName);
        }

        if (MavenCoordinates.TryParseReference(value, out var coordinate, out var extension))
        {
            return ResolveMavenPath(librariesDirectory, coordinate, extension);
        }

        return value;
    }

    public static IReadOnlyList<string> ResolveArguments(
        ForgeProcessor processor,
        IReadOnlyDictionary<string, string> tokens,
        string librariesDirectory)
    {
        var result = new List<string>(processor.Args.Count);

        foreach (var argument in processor.Args)
        {
            var resolved = ReplaceTokens(argument, tokens);

            if (MavenCoordinates.TryParseReference(resolved, out var coordinate, out var extension))
            {
                resolved = ResolveMavenPath(librariesDirectory, coordinate, extension);
            }

            result.Add(resolved);
        }

        return result;
    }

    public static IReadOnlyList<string> ResolveClasspath(
        ForgeProcessor processor,
        string librariesDirectory)
    {
        var entries = new List<string>(processor.Classpath.Count + 1);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var coordinate in new[] { processor.Jar }.Concat(processor.Classpath))
        {
            if (string.IsNullOrWhiteSpace(coordinate))
            {
                continue;
            }

            var path = ResolveMavenPath(librariesDirectory, coordinate, "jar");
            if (seen.Add(path))
            {
                entries.Add(path);
            }
        }

        return entries;
    }

    public static string ResolveMavenPath(string librariesDirectory, string coordinate, string extension)
    {
        var relative = MavenCoordinates.GetPath(coordinate, extension)
                       ?? throw new InvalidOperationException($"无法解析 Maven 坐标：{coordinate}");
        return Path.Combine(librariesDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
    }

    public static string ReplaceTokens(string value, IReadOnlyDictionary<string, string> tokens)
    {
        return TokenRegex().Replace(value, match =>
        {
            var key = match.Groups[1].Value;
            return tokens.TryGetValue(key, out var replacement) ? replacement : match.Value;
        });
    }

    [GeneratedRegex(@"\{([A-Z0-9_]+)\}", RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();
}
