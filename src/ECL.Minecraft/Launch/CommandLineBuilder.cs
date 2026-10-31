// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using Ecl.Auth;
using Ecl.Java;

namespace Ecl.Minecraft.Launch;

public static class CommandLineBuilder
{
    public static LaunchCommand Build(LaunchContext context)
    {
        var arguments = new List<string>();
        var classpath = BuildClasspath(context);
        var accessToken = context.Account.AccessToken ?? "0";
        var userType = UserType(context.Account);

        arguments.Add($"-Xmx{context.MemoryMb}M");
        arguments.Add($"-Xms{Math.Min(1024, context.MemoryMb)}M");
        arguments.Add($"-Djava.library.path={context.NativesDirectory}");
        arguments.Add("-Dminecraft.launcher.brand=EnderCraftLauncher");
        arguments.Add($"-Dminecraft.launcher.version={LauncherVersion}");

        var loggingFile = context.Version?.Logging?.Client?.File;
        var loggingArgument = context.Version?.Logging?.Client?.Argument;
        if (loggingFile is { Id.Length: > 0 } && !string.IsNullOrWhiteSpace(loggingArgument))
        {
            var loggingPath = Versions.VersionFilePlanner.GetLoggingFilePath(context.Paths, loggingFile.Id);
            // 导入的实例可能缺少 logging 资源；指向不存在的文件会让 log4j 配置加载失败。
            if (File.Exists(loggingPath))
            {
                arguments.Add(loggingArgument.Replace("${path}", loggingPath, StringComparison.OrdinalIgnoreCase));
            }
        }

        var instanceJvmArguments = context.GetInstanceSetting<string?>("instance.java.extraArguments");
        var globalJvmArguments = context.Settings.Get<string>("java.extraArguments");
        arguments.AddRange(SplitArguments(instanceJvmArguments ?? globalJvmArguments));

        var customPresetPath = context.GetInstanceSetting<string?>("instance.java.customJvmPreset")
                               ?? context.Settings.Get<string>("java.customJvmPreset");
        if (!string.IsNullOrWhiteSpace(customPresetPath))
        {
            var customPreset = CustomJvmPresetLoader.Load(customPresetPath);
            if (customPreset.IsSuccess)
            {
                arguments.AddRange(customPreset.Value!.Arguments);
            }
            else
            {
                context.JvmPresetWarning = customPreset.Error?.Message;
            }
        }

        var presetValue = context.GetInstanceSetting<string?>("instance.java.jvmPreset")
                          ?? context.Settings.Get<string>("java.jvmPreset");
        arguments.AddRange(JvmPresets.GetArguments(
            JvmPresets.Parse(presetValue),
            context.Java?.Version));

        string ReplacePlaceholders(string value) => ReplaceVersionPlaceholders(value, context, classpath);
        arguments.AddRange(VersionArgumentResolver.ResolveJvmArguments(
            context.Version?.Arguments,
            ReplacePlaceholders));

        // Authlib-Injector 只对第三方账户注入；Microsoft/离线账户这里始终为空。
        if (!string.IsNullOrWhiteSpace(context.JavaAgentArgument))
        {
            arguments.Add(context.JavaAgentArgument);
        }

        arguments.Add("-cp");
        arguments.Add(classpath);
        arguments.Add(context.Version?.MainClass ?? "net.minecraft.client.main.Main");

        arguments.Add("--username");
        arguments.Add(context.Account.Name);
        arguments.Add("--version");
        arguments.Add(context.Instance.VersionId);
        arguments.Add("--gameDir");
        arguments.Add(context.GameDirectory);
        arguments.Add("--assetsDir");
        arguments.Add(context.Paths.AssetsDirectory);
        arguments.Add("--assetIndex");
        arguments.Add(context.Version?.AssetIndex?.Id ?? context.Version?.Assets ?? "legacy");
        arguments.Add("--uuid");
        arguments.Add(context.Account.Uuid);
        arguments.Add("--accessToken");
        arguments.Add(accessToken);
        arguments.Add("--userType");
        arguments.Add(userType);
        arguments.Add("--versionType");
        arguments.Add(context.Version?.Type ?? "release");
        arguments.Add("--width");
        arguments.Add(context.Settings.Get<int>("game.windowWidth").ToString());
        arguments.Add("--height");
        arguments.Add(context.Settings.Get<int>("game.windowHeight").ToString());

        // 1.7.10 等旧版本要求 --userProperties；新版版本 JSON 不包含该选项。
        if (context.Version?.MinecraftArguments?.Contains("--userProperties", StringComparison.OrdinalIgnoreCase) == true)
        {
            arguments.Add("--userProperties");
            arguments.Add("{}");
        }

        var instanceGameArguments = context.GetInstanceSetting<string?>("instance.game.extraArguments");
        var globalGameArguments = context.Settings.Get<string>("game.extraArguments");
        arguments.AddRange(SplitArguments(instanceGameArguments ?? globalGameArguments));

        arguments.AddRange(VersionArgumentResolver.ResolveExtraMinecraftArguments(
            context.Version?.MinecraftArguments,
            ReplacePlaceholders));

        arguments.AddRange(VersionArgumentResolver.ResolveGameArguments(
            context.Version?.Arguments,
            ReplacePlaceholders));

        var display = BuildDisplay(context.JavaExecutable, arguments);
        return new LaunchCommand(context.JavaExecutable, arguments, display);
    }

    public static IReadOnlyList<string> SplitArguments(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        var result = new List<string>();
        var builder = new StringBuilder();
        var inQuotes = false;
        var quote = '"';

        foreach (var character in raw)
        {
            if (inQuotes)
            {
                if (character == quote)
                {
                    inQuotes = false;
                }
                else
                {
                    builder.Append(character);
                }

                continue;
            }

            if (character is '"' or '\'')
            {
                inQuotes = true;
                quote = character;
                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                if (builder.Length > 0)
                {
                    result.Add(builder.ToString());
                    builder.Clear();
                }

                continue;
            }

            builder.Append(character);
        }

        if (builder.Length > 0)
        {
            result.Add(builder.ToString());
        }

        return result;
    }

    public static string BuildDisplay(string executable, IReadOnlyList<string> arguments) =>
        string.Join(' ', new[] { executable }.Concat(arguments).Select(Quote));

    private static string ReplaceVersionPlaceholders(string value, LaunchContext context, string classpath)
    {
        var assetsIndex = context.Version?.AssetIndex?.Id ?? context.Version?.Assets ?? "legacy";
        var accessToken = context.Account.AccessToken ?? "0";
        var userType = UserType(context.Account);
        return value
            .Replace("${auth_player_name}", context.Account.Name, StringComparison.OrdinalIgnoreCase)
            .Replace("${version_name}", context.Instance.VersionId, StringComparison.OrdinalIgnoreCase)
            .Replace("${game_directory}", context.GameDirectory, StringComparison.OrdinalIgnoreCase)
            .Replace("${assets_root}", context.Paths.AssetsDirectory, StringComparison.OrdinalIgnoreCase)
            .Replace("${assets_index_name}", assetsIndex, StringComparison.OrdinalIgnoreCase)
            .Replace("${auth_uuid}", context.Account.Uuid, StringComparison.OrdinalIgnoreCase)
            .Replace("${auth_access_token}", accessToken, StringComparison.OrdinalIgnoreCase)
            .Replace("${auth_session}", accessToken, StringComparison.OrdinalIgnoreCase)
            .Replace("${user_type}", userType, StringComparison.OrdinalIgnoreCase)
            .Replace("${version_type}", context.Version?.Type ?? "release", StringComparison.OrdinalIgnoreCase)
            .Replace("${natives_directory}", context.NativesDirectory, StringComparison.OrdinalIgnoreCase)
            .Replace("${library_directory}", context.Paths.LibrariesDirectory, StringComparison.OrdinalIgnoreCase)
            .Replace("${classpath_separator}", Path.PathSeparator.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("${classpath}", classpath, StringComparison.OrdinalIgnoreCase)
            .Replace("${launcher_name}", "EnderCraftLauncher", StringComparison.OrdinalIgnoreCase)
            .Replace("${launcher_version}", LauncherVersion, StringComparison.OrdinalIgnoreCase)
            .Replace("${user_properties}", "{}", StringComparison.OrdinalIgnoreCase)
            .Replace("${clientid}", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("${auth_xuid}", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("${resolution_width}", context.Settings.Get<int>("game.windowWidth").ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("${resolution_height}", context.Settings.Get<int>("game.windowHeight").ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildClasspath(LaunchContext context)
    {
        if (context.Version is null)
        {
            return "<classpath>";
        }

        var plan = Versions.PlatformLibrarySelector.Plan(
            context.Version.Libraries,
            Versions.LibraryRules.CurrentOsName,
            Versions.LibraryRules.CurrentArch);

        var entries = Versions.PlatformLibrarySelector.GetClasspathArtifacts(plan)
            .Select(artifact => Versions.VersionFilePlanner.GetLibraryPath(context.Paths, artifact))
            .ToList();

        entries.Add(Versions.VersionFilePlanner.GetClientJarPath(context.Paths, context.Instance.VersionId));
        return string.Join(Path.PathSeparator, entries);
    }

    /// <summary>
    /// 第三方认证（Authlib-Injector）使用 mojang 身份类型，由注入器接管会话校验；
    /// Microsoft 账户保持 msa，离线账户保持 legacy。
    /// </summary>
    private static string UserType(Account account) => account.Type switch
    {
        AccountType.Microsoft => "msa",
        AccountType.ThirdParty => "mojang",
        _ => "legacy",
    };

    private static string Quote(string value)
    {
        if (value.Length == 0)
        {
            return "\"\"";
        }

        if (!value.Any(character => char.IsWhiteSpace(character) || character == '"'))
        {
            return value;
        }

        return $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
    }

    private static string LauncherVersion =>
        typeof(CommandLineBuilder).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
