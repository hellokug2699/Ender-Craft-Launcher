// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using Ecl.Core.Json;
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
        // 以下选项由启动器按离线账户策略处理；版本 JSON 中的占位符（clientid/xuid）
        // 在当前账户体系下没有值，不能原样传给游戏。
        "--userProperties",
        "--clientId",
        "--xuid",
    };

    private static readonly HashSet<string> LauncherManagedJvmOptions = new(StringComparer.Ordinal)
    {
        "-cp",
        "-classpath",
        "--class-path",
    };

    private static readonly string[] LauncherManagedJvmPrefixes =
    [
        "-Djava.library.path=",
        "-Dminecraft.launcher.brand=",
        "-Dminecraft.launcher.version=",
    ];

    public static IReadOnlyList<string> ResolveJvmArguments(
        VersionArguments? arguments,
        Func<string, string> replacePlaceholders,
        string? osName = null,
        string? osArch = null,
        IReadOnlyDictionary<string, bool>? features = null)
    {
        if (arguments?.Jvm is null || arguments.Jvm.Count == 0)
        {
            return [];
        }

        var tokens = FlattenArguments(
            arguments.Jvm,
            osName ?? LibraryRules.CurrentOsName,
            osArch ?? LibraryRules.CurrentArch,
            features);

        var result = new List<string>(tokens.Count);
        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            if (LauncherManagedJvmOptions.Contains(token))
            {
                // 类路径由启动器根据 rules/架构与版本文件自行构建，跳过 -cp 及其值。
                if (index + 1 < tokens.Count)
                {
                    index++;
                }

                continue;
            }

            if (LauncherManagedJvmPrefixes.Any(prefix =>
                    token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var value = replacePlaceholders(token);
            if (!HasUnresolvedPlaceholder(value))
            {
                result.Add(value);
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

            string? replacedValue = null;
            if (value is not null)
            {
                replacedValue = replacePlaceholders(value);
                if (HasUnresolvedPlaceholder(replacedValue))
                {
                    // 值无法解析时丢弃整个选项，避免给游戏留下缺值的参数。
                    continue;
                }
            }

            result.Add(token);
            if (replacedValue is not null)
            {
                result.Add(replacedValue);
            }
        }

        return result;
    }

    /// <summary>
    /// 新版加载器把启动目标放在 arguments.game 中（--launchTarget forgeclient、--fml.*）。
    /// 只提取这类加载器专属参数，避免与启动器自行拼装的原版参数重复。
    /// 规则对象同样按当前 OS/架构/feature 过滤。
    /// </summary>
    public static IReadOnlyList<string> ResolveGameArguments(
        VersionArguments? arguments,
        Func<string, string> replacePlaceholders,
        string? osName = null,
        string? osArch = null,
        IReadOnlyDictionary<string, bool>? features = null)
    {
        if (arguments?.Game is null || arguments.Game.Count == 0)
        {
            return [];
        }

        var tokens = FlattenArguments(
            arguments.Game,
            osName ?? LibraryRules.CurrentOsName,
            osArch ?? LibraryRules.CurrentArch,
            features);

        var result = new List<string>();
        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            string? value = null;
            if (index + 1 < tokens.Count &&
                !tokens[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = tokens[++index];
            }

            if (HandledGameOptions.Contains(token))
            {
                continue;
            }

            var replacedToken = replacePlaceholders(token);
            if (HasUnresolvedPlaceholder(replacedToken))
            {
                continue;
            }

            string? replacedValue = null;
            if (value is not null)
            {
                replacedValue = replacePlaceholders(value);
                if (HasUnresolvedPlaceholder(replacedValue))
                {
                    continue;
                }
            }

            result.Add(replacedToken);
            if (replacedValue is not null)
            {
                result.Add(replacedValue);
            }
        }

        return result;
    }

    /// <summary>保留旧方法名，行为与 <see cref="ResolveGameArguments"/> 一致。</summary>
    public static IReadOnlyList<string> ResolveLoaderGameArguments(
        VersionArguments? arguments,
        Func<string, string> replacePlaceholders) =>
        ResolveGameArguments(arguments, replacePlaceholders);

    private static List<string> FlattenArguments(
        IEnumerable<JsonElement> elements,
        string osName,
        string osArch,
        IReadOnlyDictionary<string, bool>? features)
    {
        var result = new List<string>();
        foreach (var element in elements)
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                var value = element.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    result.Add(value);
                }

                continue;
            }

            if (element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!VersionRuleEvaluator.IsAllowed(ReadRules(element), osName, osArch, features))
            {
                continue;
            }

            if (!element.TryGetProperty("value", out var valueElement))
            {
                continue;
            }

            if (valueElement.ValueKind == JsonValueKind.String)
            {
                var value = valueElement.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    result.Add(value);
                }
            }
            else if (valueElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in valueElement.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var value = item.GetString();
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            result.Add(value);
                        }
                    }
                }
            }
        }

        return result;
    }

    private static List<VersionRule>? ReadRules(JsonElement element)
    {
        if (!element.TryGetProperty("rules", out var rules) ||
            rules.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        try
        {
            return rules.Deserialize<List<VersionRule>>(EclJson.Default);
        }
        catch (JsonException)
        {
            // 无法解析的规则按“不允许”处理，避免把平台专属参数注入到错误平台。
            return [new VersionRule { Action = "disallow" }];
        }
    }

    private static bool HasUnresolvedPlaceholder(string value) =>
        value.Contains("${", StringComparison.Ordinal);
}
