// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using System.Text.Json;
using Ecl.Auth;
using Ecl.Configuration;
using Ecl.Configuration.Settings;
using Ecl.Core;
using Ecl.Core.Json;
using Ecl.Core.Localization;
using Ecl.Java;
using Ecl.Minecraft.Eula;
using Ecl.Minecraft.Install;
using Ecl.Minecraft.Instances;
using Ecl.Minecraft.Launch;
using Ecl.Minecraft.Loaders;
using Ecl.Minecraft.Mods;
using Ecl.Minecraft.Versions;
using Microsoft.Extensions.DependencyInjection;

namespace Ecl.Cli;

internal sealed class CliApplication
{
    private readonly IServiceProvider _provider;
    private readonly ParsedCommandLine _commandLine;

    public CliApplication(IServiceProvider provider, ParsedCommandLine commandLine)
    {
        _provider = provider;
        _commandLine = commandLine;
    }

    public async Task<int> RunAsync()
    {
        var arguments = _commandLine.Arguments;
        var command = arguments[0].ToLowerInvariant();
        var rest = arguments.Skip(1).ToArray();

        return command switch
        {
            "version" => PrintVersion(),
            "instance" => await InstanceCommandAsync(rest).ConfigureAwait(false),
            "install" => await InstallCommandAsync(rest).ConfigureAwait(false),
            "launch" => await LaunchCommandAsync(rest).ConfigureAwait(false),
            "loader" => await LoaderCommandAsync(rest).ConfigureAwait(false),
            "mod" => await ModCommandAsync(rest).ConfigureAwait(false),
            "modpack" => await ModpackCommandAsync(rest).ConfigureAwait(false),
            "pack" => await PackCommandAsync(rest).ConfigureAwait(false),
            "settings" => SettingsCommand(rest),
            "java" => await JavaCommandAsync(rest).ConfigureAwait(false),
            "account" => await AccountCommandAsync(rest).ConfigureAwait(false),
            "help" => PrintHelp(),
            _ => UsageError($"未知命令：{command}"),
        };
    }

    private int PrintVersion()
    {
        var paths = _provider.GetRequiredService<IAppPaths>();
        var version = typeof(CliApplication).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

        Console.WriteLine($"Ender Craft Launcher {version}");
        Console.WriteLine($"Runtime : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"OS      : {RuntimeInformation.OSDescription}");
        Console.WriteLine($"Data    : {paths.Root}");
        Console.WriteLine("License : GPL-3.0-or-later");
        return 0;
    }

    private async Task<int> InstanceCommandAsync(string[] args)
    {
        if (args.Length == 0 || string.Equals(args[0], "list", StringComparison.OrdinalIgnoreCase))
        {
            return await ListInstancesAsync().ConfigureAwait(false);
        }

        var instances = _provider.GetRequiredService<IInstanceService>();
        var subCommand = args[0].ToLowerInvariant();

        switch (subCommand)
        {
            case "create":
                {
                    var name = args.ElementAtOrDefault(1);
                    var version = _commandLine.GetValue("version");
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(version))
                    {
                        return UsageError("instance create 需要实例名称与 --version <游戏版本>。");
                    }

                    var loader = _commandLine.GetValue("loader") ?? "vanilla";
                    var loaderVersion = _commandLine.GetValue("loader-version");
                    var fabricApiVersion = _commandLine.GetValue("fabric-api-version");
                    var optiFineVersion = _commandLine.GetValue("optifine-version");
                    var catalog = _provider.GetRequiredService<ILoaderCatalog>();

                    if (!LoaderKinds.TryParse(loader, out var loaderKind))
                    {
                        return UsageError($"不支持的加载器：{loader}");
                    }

                    if (loaderKind == LoaderKind.Cleanroom &&
                        !string.Equals(version, "1.12.2", StringComparison.OrdinalIgnoreCase))
                    {
                        return UsageError("Cleanroom 仅支持 Minecraft 1.12.2。");
                    }

                    if (loaderKind != LoaderKind.Vanilla && string.IsNullOrWhiteSpace(loaderVersion))
                    {
                        var versions = loaderKind switch
                        {
                            LoaderKind.Fabric => await catalog.GetFabricLoadersAsync(version).ConfigureAwait(false),
                            LoaderKind.Forge => await catalog.GetForgeVersionsAsync(version).ConfigureAwait(false),
                            LoaderKind.NeoForge => await catalog.GetNeoForgeVersionsAsync(version).ConfigureAwait(false),
                            _ => await catalog.GetCleanroomVersionsAsync(version).ConfigureAwait(false),
                        };

                        if (!versions.IsSuccess)
                        {
                            return WriteError(versions.Error);
                        }

                        loaderVersion = versions.Value!.FirstOrDefault()?.Version;
                        if (string.IsNullOrWhiteSpace(loaderVersion))
                        {
                            return WriteError(new EclError("loader.no_versions", $"没有可用的 {loader} 加载器版本。"));
                        }

                        Console.WriteLine($"未指定加载器版本，使用最新版 {loaderVersion}");
                    }

                    if (loaderKind == LoaderKind.Fabric && string.IsNullOrWhiteSpace(fabricApiVersion))
                    {
                        var apiVersions = await catalog.GetFabricApiVersionsAsync(version).ConfigureAwait(false);
                        if (!apiVersions.IsSuccess)
                        {
                            return WriteError(apiVersions.Error);
                        }

                        fabricApiVersion = apiVersions.Value!.FirstOrDefault()?.Version;
                        if (string.IsNullOrWhiteSpace(fabricApiVersion))
                        {
                            return WriteError(new EclError(
                                "loader.fabric_api_missing",
                                $"游戏版本 {version} 没有可用的 Fabric API。"));
                        }

                        Console.WriteLine($"将自动安装 Fabric API {fabricApiVersion}");
                    }

                    var result = await instances
                        .CreateAsync(name, version, loader, loaderVersion, fabricApiVersion, optiFineVersion)
                        .ConfigureAwait(false);
                    if (!result.IsSuccess)
                    {
                        return WriteError(result.Error);
                    }

                    var instance = result.Value!;
                    Console.WriteLine($"已创建实例 {instance.Name}");
                    Console.WriteLine($"  ID      : {instance.Id}");
                    Console.WriteLine($"  Version : {instance.VersionId}");
                    if (!string.IsNullOrWhiteSpace(instance.FabricApiVersion))
                    {
                        Console.WriteLine($"  API     : {instance.FabricApiVersion}");
                    }

                    if (!string.IsNullOrWhiteSpace(instance.OptiFineVersion))
                    {
                        Console.WriteLine($"  OptiFine: {instance.OptiFineVersion}");
                    }

                    return 0;
                }

            case "delete":
                {
                    var instanceId = args.ElementAtOrDefault(1);
                    if (string.IsNullOrWhiteSpace(instanceId))
                    {
                        return UsageError("instance delete 需要实例 ID。");
                    }

                    if (!_commandLine.HasFlag("force"))
                    {
                        return UsageError("删除实例需要 --force 确认。");
                    }

                    var result = await instances.DeleteAsync(instanceId).ConfigureAwait(false);
                    if (!result.IsSuccess)
                    {
                        return WriteError(result.Error);
                    }

                    Console.WriteLine($"已删除实例 {instanceId}");
                    return 0;
                }

            default:
                return UsageError($"未知的 instance 子命令：{subCommand}");
        }
    }

    private async Task<int> ListInstancesAsync()
    {
        var instances = await _provider.GetRequiredService<IInstanceService>()
            .GetInstancesAsync()
            .ConfigureAwait(false);

        if (_commandLine.HasFlag("json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(instances, EclJson.Indented));
            return 0;
        }

        if (instances.Count == 0)
        {
            Console.WriteLine("暂无实例。使用 instance create 创建。");
            return 0;
        }

        foreach (var instance in instances)
        {
            var lastPlayed = instance.LastPlayedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "从未启动";
            Console.WriteLine($"{instance.Id}");
            Console.WriteLine($"  {instance.Name} · {instance.VersionId} · {instance.Loader} · {lastPlayed}");
        }

        return 0;
    }

    private async Task<int> LoaderCommandAsync(string[] args)
    {
        if (args.Length == 0 || !string.Equals(args[0], "list", StringComparison.OrdinalIgnoreCase))
        {
            return UsageError("loader 支持 list。");
        }

        var gameVersion = _commandLine.GetValue("version");
        if (string.IsNullOrWhiteSpace(gameVersion))
        {
            return UsageError("loader list 需要 --version <游戏版本>。");
        }

        gameVersion = gameVersion.Trim();
        var type = (_commandLine.GetValue("type") ?? "fabric").ToLowerInvariant();
        var catalog = _provider.GetRequiredService<ILoaderCatalog>();
        var json = _commandLine.HasFlag("json");

        switch (type)
        {
            case "fabric":
            case "forge":
            case "neoforge":
            case "cleanroom":
                {
                    var result = type switch
                    {
                        "fabric" => await catalog.GetFabricLoadersAsync(gameVersion).ConfigureAwait(false),
                        "forge" => await catalog.GetForgeVersionsAsync(gameVersion).ConfigureAwait(false),
                        "neoforge" => await catalog.GetNeoForgeVersionsAsync(gameVersion).ConfigureAwait(false),
                        _ => await catalog.GetCleanroomVersionsAsync(gameVersion).ConfigureAwait(false),
                    };

                    if (!result.IsSuccess)
                    {
                        return WriteError(result.Error);
                    }

                    if (json)
                    {
                        Console.WriteLine(JsonSerializer.Serialize(
                            result.Value!.Select(item => new
                            {
                                type,
                                gameVersion,
                                version = item.Version,
                                item.Stable,
                                releasedAt = item.ReleasedAt,
                            }),
                            EclJson.Indented));
                        return 0;
                    }

                    Console.WriteLine($"{type} 加载器版本（Minecraft {gameVersion}）：");
                    foreach (var item in result.Value!)
                    {
                        var stable = item.Stable ? "稳定" : "测试";
                        Console.WriteLine($"  {item.Version} · {FormatReleasedAt(item.ReleasedAt)} · {stable}");
                    }

                    if (result.Value!.Count == 0)
                    {
                        Console.WriteLine("  没有可用版本。");
                    }

                    return 0;
                }

            case "fabric-api":
                {
                    var result = await catalog.GetFabricApiVersionsAsync(gameVersion).ConfigureAwait(false);
                    if (!result.IsSuccess)
                    {
                        return WriteError(result.Error);
                    }

                    if (json)
                    {
                        Console.WriteLine(JsonSerializer.Serialize(
                            result.Value!.Select(item => new
                            {
                                type,
                                gameVersion,
                                version = item.Version,
                                releasedAt = item.ReleasedAt,
                                item.FileName,
                                url = item.DownloadUrl,
                            }),
                            EclJson.Indented));
                        return 0;
                    }

                    Console.WriteLine($"Fabric API 版本（Minecraft {gameVersion}）：");
                    foreach (var item in result.Value!)
                    {
                        Console.WriteLine($"  {item.Version} · {FormatReleasedAt(item.ReleasedAt)} · {item.FileName}");
                    }

                    if (result.Value!.Count == 0)
                    {
                        Console.WriteLine("  没有可用版本。");
                    }

                    return 0;
                }

            case "optifine":
                {
                    var result = await catalog.GetOptiFineVersionsAsync(gameVersion).ConfigureAwait(false);
                    if (!result.IsSuccess)
                    {
                        return WriteError(result.Error);
                    }

                    if (json)
                    {
                        Console.WriteLine(JsonSerializer.Serialize(
                            result.Value!.Select(item => new
                            {
                                type,
                                gameVersion,
                                version = item.Key,
                                releasedAt = item.ReleasedAt,
                                item.FileName,
                            }),
                            EclJson.Indented));
                        return 0;
                    }

                    Console.WriteLine($"OptiFine 版本（Minecraft {gameVersion}）：");
                    foreach (var item in result.Value!)
                    {
                        Console.WriteLine($"  {item.Key} · {FormatReleasedAt(item.ReleasedAt)} · {item.FileName}");
                    }

                    if (result.Value!.Count == 0)
                    {
                        Console.WriteLine("  没有可用版本。");
                    }

                    return 0;
                }

            default:
                return UsageError("--type 支持 fabric、forge、neoforge、cleanroom、fabric-api、optifine。");
        }
    }

    private static string FormatReleasedAt(DateTimeOffset? releasedAt) =>
        releasedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "更新时间未知";

    private async Task<int> InstallCommandAsync(string[] args)
    {
        var instanceId = args.ElementAtOrDefault(0);
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return UsageError("install 需要实例 ID。");
        }

        var installService = _provider.GetRequiredService<IVersionInstallService>();
        var localizer = _provider.GetRequiredService<ILocalizer>();
        var lastStage = (InstallStage?)null;

        var progress = new InlineProgress<InstallProgress>(update =>
        {
            if (lastStage != update.Stage)
            {
                Console.WriteLine(update.Message);
                lastStage = update.Stage;
            }

            if (update.TotalFiles > 0 &&
                (update.CompletedFiles == update.TotalFiles || update.CompletedFiles % 200 == 0))
            {
                Console.WriteLine(
                    $"  {update.CompletedFiles}/{update.TotalFiles} · {update.CompletedBytes / 1024 / 1024} MB");
            }
        });

        var result = await installService.InstallAsync(instanceId, progress).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return WriteError(result.Error);
        }

        var report = result.Value!;
        Console.WriteLine(localizer.Get("cli.install.success"));
        Console.WriteLine($"  Version   : {report.VersionId}");
        Console.WriteLine($"  Downloaded: {report.DownloadedFiles}");
        Console.WriteLine($"  Skipped   : {report.SkippedFiles}");
        Console.WriteLine($"  Bytes     : {report.Bytes}");
        Console.WriteLine($"  Duration  : {report.Duration.TotalSeconds:F1}s");
        if (report.Components is { Count: > 0 })
        {
            Console.WriteLine($"  Components: {string.Join(", ", report.Components)}");
        }

        return 0;
    }

    private async Task<int> LaunchCommandAsync(string[] args)
    {
        var instanceId = args.ElementAtOrDefault(0);
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return UsageError("launch 需要实例 ID。");
        }

        var dryRun = _commandLine.HasFlag("dry-run");
        var localizer = _provider.GetRequiredService<ILocalizer>();

        if (!dryRun)
        {
            var eula = _provider.GetRequiredService<IEulaService>();
            if (!eula.HasAccepted)
            {
                if (!_commandLine.HasFlag("accept-eula"))
                {
                    Console.Error.WriteLine(localizer.Get("eula.prompt"));
                    return WriteError(new EclError("eula.required", localizer.Get("eula.cliRequired")));
                }

                Console.WriteLine(localizer.Get("eula.prompt"));
                var accepted = eula.Accept();
                if (!accepted.IsSuccess)
                {
                    return WriteError(accepted.Error);
                }

                Console.WriteLine(localizer.Get("eula.multiplayerNotice"));
                Console.WriteLine(localizer.Get("eula.accepted"));
            }
        }

        var launchService = _provider.GetRequiredService<ILaunchService>();
        var report = await launchService.LaunchAsync(instanceId, dryRun).ConfigureAwait(false);

        if (_commandLine.HasFlag("json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(report, EclJson.Indented));
            return report.Success ? 0 : 1;
        }

        PrintReport(report);
        return report.Success ? 0 : 1;
    }

    private void PrintReport(LaunchReport report)
    {
        var localizer = _provider.GetRequiredService<ILocalizer>();
        Console.WriteLine(report.Success
            ? localizer.Get("cli.launch.success")
            : localizer.Get("cli.launch.failed"));
        Console.WriteLine($"  DryRun  : {report.DryRun}");
        Console.WriteLine($"  Duration: {report.Duration.TotalMilliseconds:F0} ms");

        foreach (var step in report.Steps)
        {
            var status = localizer.Get($"launch.status.{step.Status.ToString().ToLowerInvariant()}");
            var title = localizer.Get(step.TitleKey);
            Console.WriteLine($"  [{status}] {title}：{step.Message} ({step.Duration.TotalMilliseconds:F0} ms)");
        }

        if (!string.IsNullOrWhiteSpace(report.CommandLine))
        {
            Console.WriteLine();
            Console.WriteLine("命令：");
            Console.WriteLine(report.CommandLine);
        }
    }

    private int SettingsCommand(string[] args)
    {
        if (args.Length == 0 || string.Equals(args[0], "list", StringComparison.OrdinalIgnoreCase))
        {
            return ListSettings();
        }

        var settings = _provider.GetRequiredService<ISettingsService>();
        var localizer = _provider.GetRequiredService<ILocalizer>();
        var instanceId = _commandLine.GetValue("instance");

        if (instanceId is not null && _provider.GetRequiredService<IInstanceService>().GetAsync(instanceId).GetAwaiter().GetResult() is null)
        {
            return WriteError(new EclError("instance.not_found", $"实例不存在：{instanceId}"));
        }

        var subCommand = args[0].ToLowerInvariant();
        switch (subCommand)
        {
            case "get":
                {
                    var key = args.ElementAtOrDefault(1);
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        return UsageError("settings get 需要配置键。");
                    }

                    var value = settings.GetRawString(key, instanceId);
                    var source = settings.GetSource(key, instanceId);
                    if (_commandLine.HasFlag("json"))
                    {
                        Console.WriteLine(JsonSerializer.Serialize(new
                        {
                            key,
                            value,
                            source = source.ToString().ToLowerInvariant(),
                            instanceId,
                        }, EclJson.Indented));
                        return 0;
                    }

                    Console.WriteLine($"{value}");
                    Console.WriteLine($"source: {localizer.Get($"settings.source.{source.ToString().ToLowerInvariant()}")}");
                    return 0;
                }

            case "set":
                {
                    var key = args.ElementAtOrDefault(1);
                    if (string.IsNullOrWhiteSpace(key) || args.Length < 3)
                    {
                        return UsageError("settings set 需要配置键与值。");
                    }

                    var value = args[2];
                    if (!settings.TrySetRaw(key, value, instanceId, out var error))
                    {
                        return WriteError(new EclError("settings.invalid", error ?? "配置值无效。"));
                    }

                    Console.WriteLine($"已设置 {key} = {value}");
                    return 0;
                }

            case "clear":
                {
                    var key = args.ElementAtOrDefault(1);
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        return UsageError("settings clear 需要配置键。");
                    }

                    if (!settings.TryClear(key, instanceId, out var error))
                    {
                        return WriteError(new EclError("settings.clear_failed", error ?? "清除失败。"));
                    }

                    Console.WriteLine($"已清除 {key}");
                    return 0;
                }

            default:
                return UsageError($"未知的 settings 子命令：{subCommand}");
        }
    }

    private int ListSettings()
    {
        var settings = _provider.GetRequiredService<ISettingsService>();
        var localizer = _provider.GetRequiredService<ILocalizer>();
        var instanceId = _commandLine.GetValue("instance");
        var scope = instanceId is null ? OptionScope.Global : OptionScope.Instance;
        var definitions = settings.GetDefinitions(scope);

        if (_commandLine.HasFlag("json"))
        {
            var items = definitions.Select(definition => new
            {
                key = definition.Key,
                scope = definition.Scope.ToString().ToLowerInvariant(),
                value = settings.GetRawString(definition.Key, instanceId),
                source = settings.GetSource(definition.Key, instanceId).ToString().ToLowerInvariant(),
                requiresRestart = definition.RequiresRestart,
                kind = definition.Kind.ToString().ToLowerInvariant(),
            });

            Console.WriteLine(JsonSerializer.Serialize(items, EclJson.Indented));
            return 0;
        }

        if (definitions.Count == 0)
        {
            Console.WriteLine("当前作用域没有配置项。");
            return 0;
        }

        foreach (var definition in definitions)
        {
            var value = settings.GetRawString(definition.Key, instanceId);
            var source = settings.GetSource(definition.Key, instanceId);
            var sourceText = localizer.Get($"settings.source.{source.ToString().ToLowerInvariant()}");
            var restart = definition.RequiresRestart ? " [restart]" : string.Empty;
            Console.WriteLine($"{definition.Key} = {value}  [{sourceText}]{restart}");
        }

        return 0;
    }

    private async Task<int> JavaCommandAsync(string[] args)
    {
        if (args.Length == 0 || string.Equals(args[0], "list", StringComparison.OrdinalIgnoreCase))
        {
            return await ListJavaAsync().ConfigureAwait(false);
        }

        if (string.Equals(args[0], "install", StringComparison.OrdinalIgnoreCase))
        {
            return await InstallJavaAsync(args).ConfigureAwait(false);
        }

        return UsageError("java 支持 list 与 install。");
    }

    private async Task<int> ListJavaAsync()
    {
        var runtimes = await _provider.GetRequiredService<IJavaLocator>()
            .FindAllAsync()
            .ConfigureAwait(false);

        if (_commandLine.HasFlag("json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(runtimes, EclJson.Indented));
            return 0;
        }

        if (runtimes.Count == 0)
        {
            Console.WriteLine("未发现 Java 运行时。使用 java install <实例ID> 自动下载。");
            return 0;
        }

        foreach (var runtime in runtimes)
        {
            Console.WriteLine($"{runtime.Version?.Raw ?? "未知版本"} · {runtime.Vendor ?? "未知厂商"} · {runtime.Architecture ?? "未知架构"}");
            Console.WriteLine($"  home: {runtime.HomePath}");
            Console.WriteLine($"  exe : {runtime.ExecutablePath}");
        }

        return 0;
    }

    private async Task<int> InstallJavaAsync(string[] args)
    {
        var instanceId = args.ElementAtOrDefault(1);
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return UsageError("java install 需要实例 ID。");
        }

        var instance = await _provider.GetRequiredService<IInstanceService>()
            .GetAsync(instanceId)
            .ConfigureAwait(false);

        if (instance is null)
        {
            return WriteError(new EclError("instance.not_found", $"实例不存在：{instanceId}"));
        }

        var version = await _provider.GetRequiredService<IVersionResolver>()
            .ResolveAsync(instance.VersionId)
            .ConfigureAwait(false);

        var localizer = _provider.GetRequiredService<ILocalizer>();
        if (!version.IsSuccess)
        {
            // 新建 Forge/Cleanroom 实例时版本 JSON 可能尚未写入；仍可按 GameVersion 推断 Java 要求。
            Console.WriteLine(localizer.Get("instances.java.usingGameVersion", instance.GameVersion));
        }

        var requirement = JavaRequirements.Resolve(
            version.IsSuccess ? version.Value : null,
            instance.GameVersion);
        var lastStage = (JavaInstallStage?)null;

        var progress = new InlineProgress<JavaInstallProgress>(update =>
        {
            if (lastStage != update.Stage)
            {
                Console.WriteLine(update.Message);
                lastStage = update.Stage;
            }

            if (update.TotalFiles > 0 &&
                (update.CompletedFiles == update.TotalFiles || update.CompletedFiles % 100 == 0))
            {
                Console.WriteLine(
                    $"  {update.CompletedFiles}/{update.TotalFiles} · {update.CompletedBytes / 1024 / 1024} MB");
            }
        });

        var result = await _provider.GetRequiredService<IJavaInstallService>()
            .EnsureAsync(requirement, progress)
            .ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            return WriteError(result.Error);
        }

        var runtime = result.Value!;
        Console.WriteLine(localizer.Get("cli.java.success"));
        Console.WriteLine($"  Version: {runtime.Version?.Raw ?? "未知"}");
        Console.WriteLine($"  Home   : {runtime.HomePath}");
        Console.WriteLine($"  Exe    : {runtime.ExecutablePath}");
        return 0;
    }

    private async Task<int> AccountCommandAsync(string[] args)
    {
        if (args.Length == 0 || string.Equals(args[0], "list", StringComparison.OrdinalIgnoreCase))
        {
            return await ListAccountsAsync().ConfigureAwait(false);
        }

        var accounts = _provider.GetRequiredService<IAccountService>();
        var subCommand = args[0].ToLowerInvariant();

        switch (subCommand)
        {
            case "add-offline":
                {
                    var name = args.ElementAtOrDefault(1);
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        return UsageError("account add-offline 需要用户名。");
                    }

                    var result = await accounts.AddOfflineAsync(name, _commandLine.HasFlag("default")).ConfigureAwait(false);
                    if (!result.IsSuccess)
                    {
                        return WriteError(result.Error);
                    }

                    Console.WriteLine($"已添加离线账户 {result.Value!.Name} ({result.Value.Uuid})");
                    return 0;
                }

            case "remove":
                {
                    var accountId = args.ElementAtOrDefault(1);
                    if (string.IsNullOrWhiteSpace(accountId))
                    {
                        return UsageError("account remove 需要账户 ID。");
                    }

                    var result = await accounts.RemoveAsync(accountId).ConfigureAwait(false);
                    if (!result.IsSuccess)
                    {
                        return WriteError(result.Error);
                    }

                    Console.WriteLine($"已删除账户 {accountId}");
                    return 0;
                }

            case "default":
                {
                    var accountId = args.ElementAtOrDefault(1);
                    if (string.IsNullOrWhiteSpace(accountId))
                    {
                        return UsageError("account default 需要账户 ID。");
                    }

                    var result = await accounts.SetDefaultAsync(accountId).ConfigureAwait(false);
                    if (!result.IsSuccess)
                    {
                        return WriteError(result.Error);
                    }

                    Console.WriteLine($"已将 {accountId} 设为默认账户");
                    return 0;
                }

            default:
                return UsageError($"未知的 account 子命令：{subCommand}");
        }
    }

    private async Task<int> ListAccountsAsync()
    {
        var accounts = _provider.GetRequiredService<IAccountService>();
        var list = await accounts.GetAccountsAsync().ConfigureAwait(false);
        var defaultAccount = await accounts.GetDefaultAccountAsync().ConfigureAwait(false);

        if (_commandLine.HasFlag("json"))
        {
            var items = list.Select(account => new
            {
                account.Id,
                type = account.Type.ToString().ToLowerInvariant(),
                account.Name,
                account.Uuid,
                isDefault = account.Id == defaultAccount?.Id,
            });
            Console.WriteLine(JsonSerializer.Serialize(items, EclJson.Indented));
            return 0;
        }

        if (list.Count == 0)
        {
            Console.WriteLine("暂无账户。使用 account add-offline 添加。");
            return 0;
        }

        foreach (var account in list)
        {
            var marker = account.Id == defaultAccount?.Id ? "*" : " ";
            Console.WriteLine($"{marker} {account.Id}  {account.Name}  {account.Type}  {account.Uuid}");
        }

        return 0;
    }

    private async Task<int> ModCommandAsync(string[] args)
    {
        if (args.Length == 0)
        {
            return UsageError("mod 支持 list、check、enable、disable、enable-all、disable-all、import、delete。");
        }

        var subCommand = args[0].ToLowerInvariant();
        var instanceId = args.ElementAtOrDefault(1);
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return UsageError($"mod {subCommand} 需要实例 ID。");
        }

        var service = _provider.GetRequiredService<IModService>();
        var localizer = _provider.GetRequiredService<ILocalizer>();

        switch (subCommand)
        {
            case "list":
                {
                    var result = await service.ScanAsync(instanceId).ConfigureAwait(false);
                    if (!result.IsSuccess)
                    {
                        return WriteError(result.Error);
                    }

                    var report = result.Value!;
                    if (_commandLine.HasFlag("json"))
                    {
                        Console.WriteLine(JsonSerializer.Serialize(report, EclJson.Indented));
                        return 0;
                    }

                    Console.WriteLine(
                        $"实例 {instanceId}：{report.EnabledCount} 启用 / {report.DisabledCount} 禁用 / {report.IssueCount} 个问题");
                    foreach (var mod in report.Mods)
                    {
                        var name = mod.Metadata?.Name ?? mod.FileName;
                        var id = mod.Metadata?.ModId ?? "?";
                        var version = mod.Metadata?.Version ?? "?";
                        Console.WriteLine($"  [{(mod.Enabled ? "启用" : "禁用")}] {name} ({id} {version})");
                        Console.WriteLine($"         file: {mod.FileName}");
                        foreach (var issue in mod.Issues)
                        {
                            Console.WriteLine($"         ! {DescribeModIssue(issue, localizer)}");
                        }
                    }

                    return 0;
                }

            case "check":
                {
                    var result = await service.ScanAsync(instanceId).ConfigureAwait(false);
                    if (!result.IsSuccess)
                    {
                        return WriteError(result.Error);
                    }

                    var report = result.Value!;
                    if (_commandLine.HasFlag("json"))
                    {
                        Console.WriteLine(JsonSerializer.Serialize(report, EclJson.Indented));
                        return report.IssueCount == 0 ? 0 : 1;
                    }

                    if (report.IssueCount == 0)
                    {
                        Console.WriteLine("未发现 Mod 依赖、冲突或重复问题。");
                        return 0;
                    }

                    foreach (var issue in report.Issues)
                    {
                        Console.WriteLine($"{issue.FileName}: {DescribeModIssue(issue, localizer)}");
                    }

                    return 1;
                }

            case "enable":
            case "disable":
                {
                    var fileName = args.ElementAtOrDefault(2);
                    if (string.IsNullOrWhiteSpace(fileName))
                    {
                        return UsageError($"mod {subCommand} 需要 Mod 文件名。");
                    }

                    var result = await service
                        .SetEnabledAsync(instanceId, fileName, subCommand == "enable")
                        .ConfigureAwait(false);
                    if (!result.IsSuccess)
                    {
                        return WriteError(result.Error);
                    }

                    Console.WriteLine($"已{(subCommand == "enable" ? "启用" : "禁用")} {fileName}");
                    return 0;
                }

            case "enable-all":
            case "disable-all":
                {
                    var result = await service
                        .SetAllEnabledAsync(instanceId, subCommand == "enable-all")
                        .ConfigureAwait(false);
                    if (!result.IsSuccess)
                    {
                        return WriteError(result.Error);
                    }

                    var batch = result.Value!;
                    Console.WriteLine($"已处理 {batch.Succeeded} 个 Mod。");
                    foreach (var error in batch.Errors)
                    {
                        Console.WriteLine($"  ! {error}");
                    }

                    return batch.Errors.Count == 0 ? 0 : 1;
                }

            case "import":
                {
                    var paths = args.Skip(2).ToArray();
                    if (paths.Length == 0)
                    {
                        return UsageError("mod import 需要至少一个 .jar 文件。");
                    }

                    var result = await service.ImportAsync(instanceId, paths).ConfigureAwait(false);
                    if (!result.IsSuccess)
                    {
                        return WriteError(result.Error);
                    }

                    var batch = result.Value!;
                    Console.WriteLine($"已导入 {batch.Succeeded} 个 Mod。");
                    foreach (var error in batch.Errors)
                    {
                        Console.WriteLine($"  ! {error}");
                    }

                    return batch.Errors.Count == 0 ? 0 : 1;
                }

            case "delete":
                {
                    var fileName = args.ElementAtOrDefault(2);
                    if (string.IsNullOrWhiteSpace(fileName))
                    {
                        return UsageError("mod delete 需要 Mod 文件名。");
                    }

                    var result = await service.DeleteAsync(instanceId, fileName).ConfigureAwait(false);
                    if (!result.IsSuccess)
                    {
                        return WriteError(result.Error);
                    }

                    Console.WriteLine($"已删除 {fileName}");
                    return 0;
                }

            default:
                return UsageError($"不支持的 mod 子命令：{subCommand}");
        }
    }

    private async Task<int> ModpackCommandAsync(string[] args)
    {
        if (args.Length < 3)
        {
            return UsageError("modpack 支持 import <实例ID> <整合包> 与 export <实例ID> <输出路径>。");
        }

        var subCommand = args[0].ToLowerInvariant();
        var instanceId = args[1];
        var path = args[2];
        var service = _provider.GetRequiredService<IModpackService>();

        if (subCommand == "import")
        {
            var lastStage = string.Empty;
            var progress = new InlineProgress<ModpackProgress>(update =>
            {
                if (!string.Equals(lastStage, update.Stage, StringComparison.Ordinal))
                {
                    Console.WriteLine(update.Message);
                    lastStage = update.Stage;
                }

                if (update.Total > 0 &&
                    (update.Completed == update.Total || update.Completed % 100 == 0))
                {
                    Console.WriteLine($"  {update.Completed}/{update.Total}");
                }
            });

            var result = await service.ImportAsync(instanceId, path, progress).ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                return WriteError(result.Error);
            }

            var report = result.Value!;
            Console.WriteLine($"已导入整合包 {report.Name} {report.Version}");
            Console.WriteLine($"  Downloaded: {report.DownloadedFiles}");
            Console.WriteLine($"  Skipped   : {report.SkippedFiles}");
            Console.WriteLine($"  Overrides : {report.OverrideFiles}");
            foreach (var warning in report.Warnings)
            {
                Console.WriteLine($"  ! {warning}");
            }

            return 0;
        }

        if (subCommand == "export")
        {
            var result = await service.ExportAsync(instanceId, path).ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                return WriteError(result.Error);
            }

            Console.WriteLine($"已导出整合包到 {result.Value!.OutputPath}（{result.Value.Files} 个文件）");
            return 0;
        }

        return UsageError($"不支持的 modpack 子命令：{subCommand}");
    }

    private async Task<int> PackCommandAsync(string[] args)
    {
        if (args.Length < 2)
        {
            return UsageError("pack 支持 list、import、delete。");
        }

        var subCommand = args[0].ToLowerInvariant();
        var instanceId = args[1];
        var kind = ParseContentPackKind(_commandLine.GetValue("type"));
        var service = _provider.GetRequiredService<IContentPackService>();

        if (subCommand == "list")
        {
            var result = await service.ListAsync(instanceId, kind).ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                return WriteError(result.Error);
            }

            if (_commandLine.HasFlag("json"))
            {
                Console.WriteLine(JsonSerializer.Serialize(result.Value, EclJson.Indented));
                return 0;
            }

            if (result.Value!.Count == 0)
            {
                Console.WriteLine("没有找到资源包/光影包。");
                return 0;
            }

            foreach (var entry in result.Value)
            {
                Console.WriteLine($"{entry.FileName}");
                Console.WriteLine($"  name: {entry.Name}");
                if (entry.PackFormat is not null)
                {
                    Console.WriteLine($"  pack format: {entry.PackFormat}");
                }
            }

            return 0;
        }

        if (subCommand == "import")
        {
            var source = args.ElementAtOrDefault(2);
            if (string.IsNullOrWhiteSpace(source))
            {
                return UsageError("pack import 需要 .zip 文件。");
            }

            var result = await service.ImportAsync(instanceId, kind, source).ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                return WriteError(result.Error);
            }

            Console.WriteLine($"已导入 {result.Value!.FileName}");
            return 0;
        }

        if (subCommand == "delete")
        {
            var fileName = args.ElementAtOrDefault(2);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return UsageError("pack delete 需要文件名。");
            }

            var result = await service.DeleteAsync(instanceId, kind, fileName).ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                return WriteError(result.Error);
            }

            Console.WriteLine($"已删除 {fileName}");
            return 0;
        }

        return UsageError($"不支持的 pack 子命令：{subCommand}");
    }

    private static ContentPackKind ParseContentPackKind(string? value) =>
        string.Equals(value, "shader", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "shaders", StringComparison.OrdinalIgnoreCase)
            ? ContentPackKind.ShaderPack
            : ContentPackKind.ResourcePack;

    private static string DescribeModIssue(ModIssue issue, ILocalizer localizer) => issue.Kind switch
    {
        ModIssueKind.DuplicateModId => localizer.Get(
            "mod.issue.duplicate",
            issue.ModId ?? "?",
            issue.RelatedModId ?? "?"),
        ModIssueKind.MissingDependency => localizer.Get(
            "mod.issue.missingDependency",
            issue.ModId ?? "?",
            issue.RelatedModId ?? "?"),
        ModIssueKind.DisabledDependency => localizer.Get(
            "mod.issue.disabledDependency",
            issue.ModId ?? "?",
            issue.RelatedModId ?? "?"),
        ModIssueKind.Conflict => localizer.Get(
            "mod.issue.conflict",
            issue.ModId ?? "?",
            issue.RelatedModId ?? "?"),
        ModIssueKind.LoaderMismatch => localizer.Get(
            "mod.issue.loaderMismatch",
            issue.ModId ?? "?",
            issue.Detail ?? "?"),
        ModIssueKind.InvalidMetadata => localizer.Get(
            "mod.issue.invalidMetadata",
            issue.FileName,
            issue.Detail ?? "?"),
        _ => issue.Kind.ToString(),
    };

    private int PrintHelp()
    {
        Console.WriteLine(Program.HelpText);
        return 0;
    }

    private int UsageError(string message)
    {
        Console.Error.WriteLine(message);
        Console.Error.WriteLine();
        Console.Error.WriteLine(Program.HelpText);
        return 2;
    }

    private static int WriteError(EclError? error, int exitCode = 1)
    {
        Console.Error.WriteLine($"[{error?.Code ?? "error"}] {error?.Message ?? "未知错误"}");
        if (!string.IsNullOrWhiteSpace(error?.Detail))
        {
            Console.Error.WriteLine(error.Detail);
        }

        return exitCode;
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
