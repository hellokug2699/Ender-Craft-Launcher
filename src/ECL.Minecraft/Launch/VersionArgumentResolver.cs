// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using Ecl.Minecraft.Versions;

namespace Ecl.Minecraft.Launch;

/// <summary>
/// 处理版本 JSON 中的 arguments.jvm / arguments.game 与旧式 minecraftArguments。
/// 启动器仍自行拼装标准参数，这里只补充加载器需要的额外参数。
/// </summary>
public static class VersionArgumentResolver
{
    private static readonly HashSet<string> HandledGameOptions = new(StringComparer.Ordinal)
    {
        "--username",
        "--version",
        "--gameDir",
        "--assetsDir",
        "--assetIndex",
        "--uuid",
        "--accessToken",
        "--userType",
        "--versionType",
        "--width",
        "--height",
    };

    public static IReadOnlyList<string> ResolveJvmArguments(
        VersionArguments? arguments,
        Func<string, string> replacePlaceholders)
    {
        if (arguments?.Jvm is null || arguments.Jvm.Count == 0)
        {
            return [];
        }

        var result = new List<string>(arguments.Jvm.Count);
        foreach (var element in arguments.Jvm)
        {
            var value = ExtractString(element);
            if (!string.IsNullOrWhiteSpace(value))
            {
                result.Add(replacePlaceholders(value));
            }
        }

        return result;
    }

    /// <summary>
    /// 旧式 minecraftArguments 只补充启动器尚未处理的选项（例如 Cleanroom 的 --tweakClass）。
    /// </summary>
    public static IReadOnlyList<string> ResolveExtraMinecraftArguments(
        string? minecraftArguments,
        Func<string, string> replacePlaceholders)
    {
        if (string.IsNullOrWhiteSpace(minecraftArguments))
        {
            return [];
        }

        var tokens = CommandLineBuilder.SplitArguments(minecraftArguments);
        var result = new List<string>();

        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            string? value = null;
            if (index + 1 < tokens.Count && !tokens[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = tokens[++index];
            }

            if (HandledGameOptions.Contains(token))
            {
                continue;
            }

            result.Add(token);
            if (value is not null)
            {
                result.Add(replacePlaceholders(value));
            }
        }

        return result;
    }

    /// <summary>
    /// 新式加载器把启动目标放在 arguments.game 中（--launchTarget forgeclient、--fml.*）。
    /// 只提取这类加载器专属参数，避免与启动器自行拼装的原版参数重复。
    /// </summary>
    public static IReadOnlyList<string> ResolveLoaderGameArguments(
        VersionArguments? arguments,
        Func<string, string> replacePlaceholders)
    {
        if (arguments?.Game is null || arguments.Game.Count == 0)
        {
            return [];
        }

        var tokens = arguments.Game
            .Select(ExtractString)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => replacePlaceholders(value!))
            .ToList();

        var result = new List<string>();
        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            if (!IsLoaderOption(token))
            {
                continue;
            }

            result.Add(token);
            if (index + 1 < tokens.Count && !tokens[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                result.Add(tokens[++index]);
            }
        }

        return result;
    }

    private static bool IsLoaderOption(string token) =>
        string.Equals(token, "--launchTarget", StringComparison.OrdinalIgnoreCase) ||
        token.StartsWith("--fml.", StringComparison.OrdinalIgnoreCase);

    private static string? ExtractString(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            return element.GetString();
        }

        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty("value", out var value) &&
            value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return null;
    }
}
