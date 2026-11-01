// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Ecl.Auth;
using Ecl.Configuration;
using Ecl.Configuration.Settings;
using Ecl.Core;
using Ecl.Core.Json;
using Ecl.Core.Localization;
using Ecl.Core.Storage;
using Ecl.Deployment;
using Ecl.Java;
using Ecl.Minecraft.CurseForge;
using Ecl.Minecraft.Diagnostics;
using Ecl.Minecraft.Eula;
using Ecl.Minecraft.Install;
using Ecl.Minecraft.Instances;
using Ecl.Minecraft.Java;
using Ecl.Minecraft.Launch;
using Ecl.Minecraft.Loaders;
using Ecl.Minecraft.Mods;
using Ecl.Minecraft.Versions;
using Ecl.Operations;
using Ecl.Sessions;
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
            "workspace" => await WorkspaceCommandAsync(rest).ConfigureAwait(false),
            "session" => await SessionCommandAsync(rest).ConfigureAwait(false),
            "instance" => await InstanceCommandAsync(rest).ConfigureAwait(false),
            "install" => await InstallCommandAsync(rest).ConfigureAwait(false),
            "launch" => await LaunchCommandAsync(rest).ConfigureAwait(false),
            "loader" => await LoaderCommandAsync(rest).ConfigureAwait(false),
            "mod" => await ModCommandAsync(rest).ConfigureAwait(false),
            "modpack" => await ModpackCommandAsync(rest).ConfigureAwait(false),
            "curseforge" => await CurseForgeCommandAsync(rest).ConfigureAwait(false),
            "pack" => await PackCommandAsync(rest).ConfigureAwait(false),
            "crash" => await CrashCommandAsync(rest).ConfigureAwait(false),
            "diagnostics" => await DiagnosticsCommandAsync(rest).ConfigureAwait(false),
            "settings" => SettingsCommand(rest),
            "update" => await UpdateCommandAsync(rest).ConfigureAwait(false),
            "java" => await JavaCommandAsync(rest).ConfigureAwait(false),
            "account" => await AccountCommandAsync(rest).ConfigureAwait(false),
            "authserver" => await AuthServerCommandAsync(rest).ConfigureAwait(false),
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
        Console.WriteLine($"Game    : {paths.GameRoot}");
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
                    if (await RequireManagedWorkspaceAsync().ConfigureAwait(false) is { } workspaceError)
                    {
                        return workspaceError;
                    }

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

            case "scan":
                {
                    var detected = await instances.ScanVersionsAsync().ConfigureAwait(false);
                    if (_commandLine.HasFlag("json"))
                    {
                        Console.WriteLine(JsonSerializer.Serialize(detected, EclJson.Indented));
                        return 0;
                    }

                    if (detected.Count == 0)
                    {
                        Console.WriteLine("未在 versions 目录发现可导入的版本。");
                        return 0;
                    }

                    foreach (var version in detected)
                    {
                        var isolation = version.Isolated ? "隔离" : "未隔离";
                        var importedMark = version.AlreadyImported ? "  [已导入]" : string.Empty;
                        var problemMark = version.Problem switch
                        {
                            DetectedVersionProblem.InvalidManifest => "  [无效]",
                            DetectedVersionProblem.IncompleteInheritance => "  [不完整]",
                            _ => string.Empty,
                        };
                        Console.WriteLine($"  {version.VersionId}  ·  {version.Loader}  ·  {isolation}{importedMark}{problemMark}");
                        Console.WriteLine($"      游戏目录：{version.GameDirectory}");
                        if (!string.IsNullOrWhiteSpace(version.ProblemDetail))
                        {
                            Console.WriteLine($"      原因：{version.ProblemDetail}");
                        }
                    }

                    return 0;
                }

            case "import":
                {
                    var versionId = args.ElementAtOrDefault(1);
                    if (string.IsNullOrWhiteSpace(versionId))
                    {
                        return UsageError("instance import 需要 versions 目录下的版本 ID。");
                    }

                    var imported = await instances
                        .ImportAsync(versionId, _commandLine.GetValue("name"))
                        .ConfigureAwait(false);
                    if (!imported.IsSuccess)
                    {
                        return WriteError(imported.Error);
                    }

                    Console.WriteLine($"已导入实例 {imported.Value!.Name}");
                    Console.WriteLine($"  ID      : {imported.Value.Id}");
                    Console.WriteLine($"  版本    : {imported.Value.VersionId} ({imported.Value.Loader})");
                    Console.WriteLine($"  游戏目录: {imported.Value.GameDirectory}");
                    return 0;
                }

            case "delete":
                {
                    var instanceId = args.ElementAtOrDefault(1);
                    if (string.IsNullOrWhiteSpace(instanceId))
                    {
                        return UsageError("instance delete 需要实例 ID。");
                    }

                    var planResult = await instances.GetDeletionPlanAsync(instanceId).ConfigureAwait(false);
                    if (!planResult.IsSuccess)
                    {
                        return WriteError(planResult.Error);
                    }

                    var plan = planResult.Value!;
                    PrintDeletionPlan(plan, _commandLine.HasFlag("json"));

                    if (_commandLine.HasFlag("dry-run"))
                    {
                        return 0;
                    }

                    if (!_commandLine.HasFlag("force"))
                    {
                        return UsageError("删除实例需要 --force 确认；使用 --keep-data 可只移除启动器记录。");
                    }

                    var mode = _commandLine.HasFlag("keep-data")
                        ? InstanceDeletionMode.RemoveRecordOnly
                        : InstanceDeletionMode.DeleteManagedData;
                    var result = await instances
                        .DeleteAsync(instanceId, new InstanceDeleteOptions { Mode = mode })
                        .ConfigureAwait(false);
                    if (!result.IsSuccess)
                    {
                        return WriteError(result.Error);
                    }

                    var keptGameData = plan.HasManagedGameData && mode == InstanceDeletionMode.RemoveRecordOnly;
                    Console.WriteLine(keptGameData
                        ? $"已移除实例记录 {instanceId}，游戏数据保留在 {plan.GameDirectory}"
                        : plan.HasManagedGameData
                            ? $"已删除实例 {instanceId}（含 ECL 托管游戏数据）"
                            : $"已移除实例记录 {instanceId}，外部游戏目录未删除：{plan.GameDirectory}");
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

    private static void PrintDeletionPlan(InstanceDeletionPlan plan, bool json)
    {
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(plan, EclJson.Indented));
            return;
        }

        Console.WriteLine($"实例：{plan.InstanceName}（{plan.InstanceId}）");
        Console.WriteLine($"  启动器记录：{plan.RecordDirectory}");
        if (plan.Find(InstanceDeletionEntryKind.ManagedGameData) is { } managed)
        {
            Console.WriteLine($"  ECL 托管游戏目录：{managed.Path}（{(managed.Exists ? "存在" : "不存在")}）");
            Console.WriteLine("  默认删除托管数据；--keep-data 只移除记录并保留游戏数据。");
        }
        else if (plan.Find(InstanceDeletionEntryKind.ExternalGameData) is { } external)
        {
            Console.WriteLine($"  外部游戏目录：{external.Path}（只引用，不会删除）");
            if (plan.IsShared)
            {
                Console.WriteLine(
                    $"  共享引用：{string.Join("、", plan.SharedWith.Select(reference => $"{reference.Name}（{reference.Id}）"))}");
            }
        }
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

        if (await RequireManagedWorkspaceAsync().ConfigureAwait(false) is { } workspaceError)
        {
            return workspaceError;
        }

        var installService = _provider.GetRequiredService<IVersionInstallService>();
        var localizer = _provider.GetRequiredService<ILocalizer>();

        using var operations = TrackOperations();
        var result = await installService.InstallAsync(instanceId).ConfigureAwait(false);
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

        if (await RequireManagedWorkspaceAsync().ConfigureAwait(false) is { } launchWorkspaceError)
        {
            return launchWorkspaceError;
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
        var report = await launchService
            .LaunchAsync(instanceId, dryRun, _commandLine.HasFlag("accept-java-risk"))
            .ConfigureAwait(false);

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
            Console.WriteLine(SecretRedaction.CommandLine(report.CommandLine));
        }

        if (report.JavaPlan is { } java)
        {
            Console.WriteLine();
            Console.WriteLine(localizer.Get("cli.launch.javaTitle"));
            Console.WriteLine($"  {localizer.Get("cli.launch.javaPath")}: {java.ExecutablePath ?? "-"}");
            Console.WriteLine(
                $"  {localizer.Get("cli.launch.javaVersion")}: {java.Version ?? "-"} ({java.Architecture ?? "-"})");
            Console.WriteLine(
                $"  {localizer.Get("cli.launch.javaRequirement")}: {java.RequirementSummary ?? "-"}");
            Console.WriteLine(
                $"  {localizer.Get("cli.launch.javaReason")}: {java.SelectionReason} / {java.CompatibilityStatus}");
            if (java.IsExplicitOverride)
            {
                Console.WriteLine($"  {localizer.Get("cli.launch.javaPinned")}: true");
            }

            foreach (var candidate in java.EvaluatedCandidates.Where(candidate =>
                         !string.Equals(candidate.Status, "Selected", StringComparison.Ordinal)))
            {
                Console.WriteLine(
                    $"  {localizer.Get("cli.launch.javaRejected")}: {candidate.ExecutablePath} " +
                    $"[{candidate.Status}] {candidate.Detail}");
            }
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
        var definitions = settings.GetDefinitions(scope)
            .Where(definition => !definition.IsHidden)
            .ToArray();

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

        if (definitions.Length == 0)
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

    private async Task<int> UpdateCommandAsync(string[] args)
    {
        if (args.Length > 1 ||
            (args.Length == 1 && !string.Equals(args[0], "check", StringComparison.OrdinalIgnoreCase)))
        {
            return UsageError("update 支持 check。");
        }

        var settings = _provider.GetRequiredService<ISettingsService>();
        if (!settings.Get<bool>("launcher.checkUpdates"))
        {
            Console.WriteLine("更新检查已关闭（launcher.checkUpdates=false）。");
            return 0;
        }

        var channelValue = settings.Get<string>("launcher.updateChannel");
        var channel = UpdateChannels.TryParse(channelValue, out var parsedChannel)
            ? parsedChannel
            : UpdateChannel.Stable;

        var feedValue = _commandLine.GetValue("feed") ??
                        Environment.GetEnvironmentVariable("ECL_UPDATE_FEED");
        if (string.IsNullOrWhiteSpace(feedValue))
        {
            Console.WriteLine("未配置更新源。设置 ECL_UPDATE_FEED 或传入 --feed <https URL> 后重试。");
            return 0;
        }

        if (!Uri.TryCreate(feedValue, UriKind.Absolute, out var feed) ||
            feed.Scheme != Uri.UriSchemeHttps)
        {
            return WriteError(new EclError("update.insecure_feed", "更新源必须是 HTTPS 地址。"));
        }

        var trustPath = _commandLine.GetValue("trust") ??
                        Path.Combine(AppContext.BaseDirectory, "update-trust.json");
        var trust = await UpdateTrustStore.FromFileAsync(trustPath).ConfigureAwait(false);
        if (!trust.IsSuccess)
        {
            return WriteError(trust.Error);
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var client = new UpdateClient(http, new UpdateSignatureVerifier(trust.Value!));
        var check = await client.CheckAsync(feed, channel).ConfigureAwait(false);
        if (!check.IsSuccess)
        {
            return WriteError(check.Error);
        }

        var manifest = check.Value!.Manifest;
        var current = await GetCurrentDeploymentVersionAsync().ConfigureAwait(false);
        var available = current is null || manifest.Version.CompareTo(current) > 0;
        if (_commandLine.HasFlag("json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                channel = channel.ToApiValue(),
                current = current?.ToString(),
                available,
                version = manifest.Version.ToString(),
                package = manifest.Package.FileName,
                packageSize = manifest.Package.Size,
                manifest = check.Value.ManifestUri.ToString(),
            }, EclJson.Indented));
            return 0;
        }

        Console.WriteLine($"通道：{channel.ToApiValue()}");
        Console.WriteLine($"当前版本：{current?.ToString() ?? "未知"}");
        if (available)
        {
            Console.WriteLine($"发现新版本：{manifest.Version}");
            Console.WriteLine($"更新包：{UpdateFeed.GetPackageUri(check.Value.ManifestUri, manifest.Package)}");
            Console.WriteLine($"大小：{manifest.Package.Size} 字节");
        }
        else
        {
            Console.WriteLine("当前已是最新版本。");
        }

        return 0;
    }

    private async Task<ReleaseVersion?> GetCurrentDeploymentVersionAsync()
    {
        var installRoot = _commandLine.GetValue("install-root");
        if (!string.IsNullOrWhiteSpace(installRoot))
        {
            var current = await new UpdateLayout(installRoot).ReadCurrentAsync().ConfigureAwait(false);
            if (current.IsSuccess)
            {
                return current.Value!.ParsedVersion;
            }
        }

        var version = typeof(CliApplication).Assembly.GetName().Version;
        return version is null
            ? null
            : ReleaseVersion.Parse($"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}");
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

        if (string.Equals(args[0], "why", StringComparison.OrdinalIgnoreCase))
        {
            return await ExplainJavaAsync(args).ConfigureAwait(false);
        }

        return UsageError("java 支持 list、install 与 why。");
    }

    /// <summary>
    /// “Why this Java?”：展示有效要求、选择原因、固定/自动模式与被拒候选。
    /// 与 WPF 实例详情使用同一个 Core 选择结果，避免两套规则。
    /// </summary>
    private async Task<int> ExplainJavaAsync(string[] args)
    {
        var instanceId = args.ElementAtOrDefault(1);
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return UsageError("java why 需要实例 ID。");
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
        var selection = await _provider.GetRequiredService<IJavaSelectionService>()
            .ResolveAsync(instance, version.IsSuccess ? version.Value : null)
            .ConfigureAwait(false);
        var decision = selection.Decision;

        if (_commandLine.HasFlag("json"))
        {
            WriteJson(new
            {
                instance = new { instance.Id, instance.Name, instance.GameVersion },
                selection,
                decision,
                plan = selection.Plan,
            });
            return 0;
        }

        Console.WriteLine($"实例      : {instance.Name} ({instance.Id})");
        Console.WriteLine($"游戏版本  : {instance.GameVersion}");
        Console.WriteLine($"要求      : {selection.Requirement?.Describe() ?? decision?.Requirements?.Describe() ?? "未知"}");
        if (decision?.Requirements is { } requirements)
        {
            Console.WriteLine($"要求来源  : {requirements.Explain()}");
        }

        var runtime = selection.Runtime ?? decision?.Runtime;
        if (runtime is null)
        {
            Console.WriteLine("结果      : 没有可用的兼容 Java");
        }
        else
        {
            Console.WriteLine($"结果      : {runtime.Version?.Raw ?? "未知版本"} · {runtime.Vendor ?? "未知厂商"} · {runtime.ArchitectureLabel}");
            Console.WriteLine($"路径      : {runtime.ExecutablePath}");
        }

        Console.WriteLine($"选择方式  : {(selection.IsExplicitOverride ? "实例固定（不会被静默替换）" : "按全局优先级自动选择")}");
        Console.WriteLine($"选择原因  : {decision?.Explanation ?? selection.Plan?.SelectionReason ?? "未知"}");
        Console.WriteLine($"兼容状态  : {selection.Plan?.CompatibilityStatus ?? decision?.Reason.ToString() ?? "未知"}");
        if (decision?.RequiresRiskAcknowledgement == true)
        {
            Console.WriteLine($"风险确认  : 需要（{decision.RiskIdentity ?? "未知身份"}）");
        }

        if (selection.IsBlocked || decision?.IsBlocked == true)
        {
            Console.WriteLine($"已阻止    : {selection.BlockReason ?? decision?.Failure.ToString()}");
        }

        var rejected = decision?.RejectedCandidates ?? [];
        if (rejected.Count > 0)
        {
            Console.WriteLine("未选候选  :");
            foreach (var candidate in rejected)
            {
                Console.WriteLine(
                    $"  - {candidate.Runtime.Version?.Raw ?? "未知版本"} · {candidate.Runtime.ExecutablePath}");
                Console.WriteLine($"    {candidate.Status}: {candidate.Detail}");
            }
        }

        if (selection.Warnings.Count > 0)
        {
            Console.WriteLine("警告      :");
            foreach (var warning in selection.Warnings)
            {
                Console.WriteLine($"  ! {warning}");
            }
        }

        return selection.IsBlocked ? 1 : 0;
    }

    private async Task<int> ListJavaAsync()
    {
        var locator = _provider.GetRequiredService<IJavaLocator>();
        var runtimes = await locator.FindAllAsync().ConfigureAwait(false);
        var settings = _provider.GetRequiredService<ISettingsService>();
        var localizer = _provider.GetRequiredService<ILocalizer>();
        var configured = JavaSearchPath.Split(settings.Get<string>("java.defaultPath"));
        var candidates = configured
            .Select(path => locator.FromPath(path))
            .Where(runtime => runtime is not null)
            .Select(runtime => runtime!)
            .Concat(runtimes)
            .GroupBy(runtime => runtime.ExecutablePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        var priority = JavaPriorityOrdering.Order(candidates, configured);

        if (_commandLine.HasFlag("json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(priority.Runtimes, EclJson.Indented));
            return 0;
        }

        if (priority.Runtimes.Count == 0)
        {
            Console.WriteLine("未发现 Java 运行时。使用 java install <实例ID> 自动下载。");
            return 0;
        }

        for (var index = 0; index < priority.Runtimes.Count; index++)
        {
            var runtime = priority.Runtimes[index];
            var tag = index < priority.ConfiguredCount
                ? $" [{localizer.Get("java.manager.priorityTag")}]"
                : string.Empty;
            var support = runtime.IsKnown32Bit
                ? $" · {localizer.Get("java.manager.unsupported32")}"
                : runtime.ResolvedBitness == JavaBitness.Unknown
                    ? $" · {localizer.Get("java.manager.unknownArch")}"
                    : string.Empty;
            Console.WriteLine(
                $"{runtime.Version?.Raw ?? "未知版本"} · {runtime.Vendor ?? "未知厂商"} · " +
                $"{runtime.ArchitectureLabel}{support}{tag}");
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

        var requirements = await _provider.GetRequiredService<IJavaSelectionService>()
            .ResolveRequirementsAsync(instance, version.IsSuccess ? version.Value : null)
            .ConfigureAwait(false);
        if (requirements.HasConflicts)
        {
            return WriteError(new EclError("java.requirements_conflict", requirements.Explain()));
        }

        if (requirements.IsUnknown)
        {
            Console.WriteLine(localizer.Get("instances.java.unknownRequirement", instance.GameVersion));
        }

        var requirement = requirements.ToRequirement();

        using var operations = TrackOperations();
        var result = await _provider.GetRequiredService<IJavaInstallService>()
            .EnsureAsync(requirement)
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

            case "login-thirdparty":
                {
                    var servers = _provider.GetRequiredService<IAuthServerService>();
                    var serverArg = args.ElementAtOrDefault(1);
                    if (string.IsNullOrWhiteSpace(serverArg))
                    {
                        return UsageError("account login-thirdparty 需要认证服务器 ID 或地址。");
                    }

                    var username = _commandLine.GetValue("username");
                    if (string.IsNullOrWhiteSpace(username))
                    {
                        return UsageError("account login-thirdparty 需要 --username。");
                    }

                    var server = await servers.ResolveAsync(serverArg).ConfigureAwait(false);
                    if (!server.IsSuccess)
                    {
                        return WriteError(server.Error);
                    }

                    // 密码只用于本次认证：不回显、不写入日志、不持久化。
                    var password = ReadPassword(
                        $"请输入 {username} 在 {server.Value!.Name} 的密码（不会回显，也不会保存）：");
                    if (string.IsNullOrEmpty(password))
                    {
                        return WriteError(new EclError(
                            "auth.thirdparty_credentials_required",
                            "密码不能为空。"));
                    }

                    var signedIn = await accounts
                        .SignInThirdPartyAsync(
                            server.Value.Id,
                            username,
                            password,
                            _commandLine.HasFlag("default"))
                        .ConfigureAwait(false);
                    if (!signedIn.IsSuccess)
                    {
                        return WriteError(signedIn.Error);
                    }

                    Console.WriteLine($"第三方登录成功：{signedIn.Value!.Name}");
                    Console.WriteLine($"  UUID   : {signedIn.Value.Uuid}");
                    Console.WriteLine($"  服务器 : {server.Value.Name} ({server.Value.Id})");
                    return 0;
                }

            case "login-microsoft":
                {
                    var authentication = _provider.GetRequiredService<IMicrosoftAuthenticationService>();
                    var clientId = MicrosoftClientIdentity.DefaultClientId;

                    // 仅供实机验证的诊断入口：必须由用户主动触发，不会在启动时自动登录。
                    Console.WriteLine($"Microsoft Client ID: {clientId}");
                    Console.WriteLine("按回车后 ECL 将打开系统默认浏览器；请在浏览器中自行完成 Microsoft 登录。");
                    Console.ReadLine();

                    Result<Account> result;
                    using (var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5)))
                    {
                        try
                        {
                            result = await authentication
                                .SignInAsync(new MicrosoftAuthenticationOptions { ClientId = clientId }, OpenBrowserAsync, timeout.Token)
                                .ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            return WriteError(new EclError("auth.callback_timeout", "等待浏览器回调超时（5 分钟），未收到授权码。"));
                        }
                    }

                    if (!result.IsSuccess)
                    {
                        return WriteError(result.Error);
                    }

                    var added = await accounts
                        .AddAsync(result.Value!, _commandLine.HasFlag("default"))
                        .ConfigureAwait(false);
                    if (!added.IsSuccess)
                    {
                        return WriteError(added.Error);
                    }

                    Console.WriteLine($"Microsoft 账户登录成功：{added.Value!.Name}");
                    Console.WriteLine($"  Minecraft UUID: {added.Value.Uuid}");
                    Console.WriteLine($"  ECL 账户 ID   : {added.Value.Id}");
                    return 0;
                }

            case "refresh":
                {
                    var clientId = MicrosoftClientIdentity.DefaultClientId;

                    var accountId = args.ElementAtOrDefault(1);
                    if (string.IsNullOrWhiteSpace(accountId))
                    {
                        var defaultAccount = await accounts.GetDefaultAccountAsync().ConfigureAwait(false);
                        if (defaultAccount is null)
                        {
                            return WriteError(new EclError("account.not_found", "没有可刷新的默认账户。"));
                        }

                        accountId = defaultAccount.Id;
                    }

                    Console.WriteLine($"静默刷新账户 {accountId}（不会打开浏览器）…");
                    var refreshed = await accounts
                        .RefreshAsync(accountId, new MicrosoftAuthenticationOptions { ClientId = clientId })
                        .ConfigureAwait(false);
                    if (!refreshed.IsSuccess)
                    {
                        return WriteError(refreshed.Error);
                    }

                    Console.WriteLine($"刷新成功：{refreshed.Value!.Name}");
                    if (refreshed.Value.MinecraftTokenExpiresAt is { } expiresAt)
                    {
                        Console.WriteLine($"  Minecraft token 有效期至：{expiresAt.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}");
                    }

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
        var servers = _provider.GetRequiredService<IAuthServerService>();
        var list = await accounts.GetAccountsAsync().ConfigureAwait(false);
        var defaultAccount = await accounts.GetDefaultAccountAsync().ConfigureAwait(false);
        var serverById = (await servers.GetServersAsync().ConfigureAwait(false))
            .ToDictionary(server => server.Id, StringComparer.Ordinal);

        string DescribeServer(Account account) =>
            account.AuthServerId is { Length: > 0 } id
                ? serverById.TryGetValue(id, out var server) ? server.Name : id
                : string.Empty;

        if (_commandLine.HasFlag("json"))
        {
            var items = list.Select(account => new
            {
                account.Id,
                type = account.Type.ToString().ToLowerInvariant(),
                account.Name,
                account.Uuid,
                account.AuthServerId,
                authServer = DescribeServer(account),
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
            var server = DescribeServer(account);
            var suffix = server.Length > 0 ? $"  @{server}" : string.Empty;
            Console.WriteLine($"{marker} {account.Id}  {account.Name}  {account.Type}  {account.Uuid}{suffix}");
        }

        return 0;
    }

    private async Task<int> AuthServerCommandAsync(string[] args)
    {
        var servers = _provider.GetRequiredService<IAuthServerService>();

        if (args.Length == 0 || string.Equals(args[0], "list", StringComparison.OrdinalIgnoreCase))
        {
            var list = await servers.GetServersAsync().ConfigureAwait(false);
            if (_commandLine.HasFlag("json"))
            {
                Console.WriteLine(JsonSerializer.Serialize(list, EclJson.Indented));
                return 0;
            }

            if (list.Count == 0)
            {
                Console.WriteLine("暂无认证服务器。使用 authserver add <地址> 添加。");
                return 0;
            }

            foreach (var server in list)
            {
                Console.WriteLine($"{server.Id}  {server.Name}  {server.Url}");
            }

            return 0;
        }

        switch (args[0].ToLowerInvariant())
        {
            case "add":
                {
                    var url = args.ElementAtOrDefault(1);
                    if (string.IsNullOrWhiteSpace(url))
                    {
                        return UsageError("authserver add 需要认证服务器 API 根地址。");
                    }

                    var registerUrl = _commandLine.GetValue("register");
                    var added = await servers.AddAsync(url, registerUrl).ConfigureAwait(false);
                    if (!added.IsSuccess)
                    {
                        return WriteError(added.Error);
                    }

                    Console.WriteLine($"已添加认证服务器 {added.Value!.Name}");
                    Console.WriteLine($"  ID  : {added.Value.Id}");
                    Console.WriteLine($"  API : {added.Value.Url}");
                    if (!string.IsNullOrWhiteSpace(added.Value.RegisterUrl))
                    {
                        Console.WriteLine($"  注册: {added.Value.RegisterUrl}");
                    }

                    return 0;
                }

            case "update":
                {
                    var id = args.ElementAtOrDefault(1);
                    if (string.IsNullOrWhiteSpace(id))
                    {
                        return UsageError("authserver update 需要认证服务器 ID。");
                    }

                    var url = _commandLine.GetValue("url");
                    var registerUrl = _commandLine.GetValue("register");
                    var updated = await servers.UpdateAsync(id, url, registerUrl).ConfigureAwait(false);
                    if (!updated.IsSuccess)
                    {
                        return WriteError(updated.Error);
                    }

                    Console.WriteLine($"已更新认证服务器 {updated.Value!.Name}");
                    Console.WriteLine($"  API : {updated.Value.Url}");
                    Console.WriteLine($"  注册: {updated.Value.RegisterUrl ?? "(未设置)"}");
                    return 0;
                }

            case "remove":
                {
                    var id = args.ElementAtOrDefault(1);
                    if (string.IsNullOrWhiteSpace(id))
                    {
                        return UsageError("authserver remove 需要认证服务器 ID。");
                    }

                    var removed = await servers.RemoveAsync(id).ConfigureAwait(false);
                    if (!removed.IsSuccess)
                    {
                        return WriteError(removed.Error);
                    }

                    Console.WriteLine($"已删除认证服务器 {id}");
                    return 0;
                }

            default:
                return UsageError($"未知的 authserver 子命令：{args[0]}");
        }
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
            if (await RequireManagedWorkspaceAsync().ConfigureAwait(false) is { } workspaceError)
            {
                return workspaceError;
            }

            using var operations = TrackOperations();
            var result = await service.ImportAsync(instanceId, path).ConfigureAwait(false);
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

    private async Task<int> CurseForgeCommandAsync(string[] args)
    {
        var subCommand = args.Length == 0 ? "status" : args[0].ToLowerInvariant();
        var service = _provider.GetRequiredService<ICurseForgeService>();

        switch (subCommand)
        {
            case "status":
                return await CurseForgeStatusAsync(service).ConfigureAwait(false);
            case "search":
                return await CurseForgeSearchAsync(service, args).ConfigureAwait(false);
            case "files":
                return await CurseForgeFilesAsync(args).ConfigureAwait(false);
            case "install":
                return await CurseForgeInstallAsync(service, args).ConfigureAwait(false);
            default:
                return UsageError($"不支持的 curseforge 子命令：{subCommand}");
        }
    }

    private async Task<int> CurseForgeStatusAsync(ICurseForgeService service)
    {
        var status = service.Status;
        var endpoint = CurseForgeClient.BaseAddress + CurseForgeClient.ApiVersionPath;
        var checkResult = (Result<CurseForgeSearchResult>?)null;
        if (_commandLine.HasFlag("check"))
        {
            checkResult = await service
                .SearchAsync(new CurseForgeSearchQuery { PageSize = 1 })
                .ConfigureAwait(false);
        }

        if (_commandLine.HasFlag("json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                configured = status.IsConfigured,
                source = status.Source.ToString().ToLowerInvariant(),
                endpoint,
                checkedApi = checkResult.HasValue,
                checkSucceeded = checkResult?.IsSuccess,
                checkError = checkResult?.Error?.Message,
            }, EclJson.Indented));
            return checkResult is not { } check || check.IsSuccess ? 0 : 1;
        }

        Console.WriteLine("CurseForge API：通过 ECL 代理访问");
        Console.WriteLine($"  端点：{endpoint}");
        if (checkResult is null)
        {
            return 0;
        }

        if (!checkResult.Value.IsSuccess)
        {
            return WriteError(checkResult.Value.Error);
        }

        Console.WriteLine("  API 连通性：正常");
        return 0;
    }

    private async Task<int> CurseForgeSearchAsync(ICurseForgeService service, string[] args)
    {
        if (!TryGetCurseForgeLoader(out var loader))
        {
            return UsageError("--loader 支持 fabric、forge、neoforge、quilt 或 any。");
        }

        var query = args.ElementAtOrDefault(1) ?? string.Empty;
        var gameVersion = _commandLine.GetValue("game-version") ?? _commandLine.GetValue("version");
        var pageSize = Math.Clamp(GetIntOption("page-size", 20), 1, 50);
        var index = Math.Max(0, GetIntOption("index", 0));

        var result = await service.SearchAsync(
                new CurseForgeSearchQuery
                {
                    SearchFilter = query,
                    GameVersion = gameVersion,
                    Loader = loader,
                    Index = index,
                    PageSize = pageSize,
                })
            .ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return WriteError(result.Error);
        }

        var search = result.Value!;
        if (_commandLine.HasFlag("json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                totalCount = search.TotalCount,
                index = search.Index,
                pageSize = search.PageSize,
                mods = search.Mods.Select(mod => new
                {
                    mod.Id,
                    mod.Slug,
                    mod.Name,
                    mod.Summary,
                    mod.DownloadCount,
                    mod.DateModified,
                    mod.WebsiteUrl,
                }),
            }, EclJson.Indented));
            return 0;
        }

        Console.WriteLine($"CurseForge 搜索结果：{search.TotalCount} 个（显示 {search.Mods.Count} 个）");
        foreach (var mod in search.Mods)
        {
            Console.WriteLine($"  {mod.Id}  {mod.Slug}  {mod.Name}");
            Console.WriteLine($"        下载 {mod.DownloadCount:N0} · {FormatReleasedAt(mod.DateModified)}");
        }

        if (search.Mods.Count == 0)
        {
            Console.WriteLine("  没有匹配的 Mod。");
        }

        return 0;
    }

    private async Task<int> CurseForgeFilesAsync(string[] args)
    {
        var reference = args.ElementAtOrDefault(1);
        if (string.IsNullOrWhiteSpace(reference))
        {
            return UsageError("curseforge files 需要项目 ID 或 slug。");
        }

        if (!TryGetCurseForgeLoader(out var loader))
        {
            return UsageError("--loader 支持 fabric、forge、neoforge、quilt 或 any。");
        }

        var client = _provider.GetRequiredService<ICurseForgeClient>();
        var modResult = await ResolveCurseForgeModAsync(client, reference).ConfigureAwait(false);
        if (!modResult.IsSuccess)
        {
            return WriteError(modResult.Error);
        }

        var gameVersion = _commandLine.GetValue("game-version") ?? _commandLine.GetValue("version");
        var pageSize = Math.Clamp(GetIntOption("page-size", 20), 1, 50);
        var filesResult = await client
            .GetFilesForModAsync(modResult.Value!.Id, gameVersion, loader, pageSize)
            .ConfigureAwait(false);
        if (!filesResult.IsSuccess)
        {
            return WriteError(filesResult.Error);
        }

        if (_commandLine.HasFlag("json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(filesResult.Value, EclJson.Indented));
            return 0;
        }

        Console.WriteLine($"{modResult.Value!.Name} 的文件：");
        foreach (var file in filesResult.Value!)
        {
            Console.WriteLine($"  {file.Id}  {file.FileName}");
            Console.WriteLine(
                $"        {FormatCurseForgeReleaseType(file.ReleaseType)} · {FormatReleasedAt(file.FileDate)} · {file.FileLength / 1024} KB");
            Console.WriteLine($"        {string.Join(", ", file.GameVersions)}");
        }

        return 0;
    }

    private async Task<int> CurseForgeInstallAsync(ICurseForgeService service, string[] args)
    {
        var instanceId = args.ElementAtOrDefault(1);
        var reference = args.ElementAtOrDefault(2);
        if (string.IsNullOrWhiteSpace(instanceId) || string.IsNullOrWhiteSpace(reference))
        {
            return UsageError("curseforge install 需要实例 ID 与项目 ID/slug/链接。");
        }

        int? fileId = int.TryParse(_commandLine.GetValue("file-id"), out var parsedFileId) && parsedFileId > 0
            ? parsedFileId
            : null;

        using var operations = TrackOperations();
        var result = await service
            .InstallAsync(
                instanceId,
                reference,
                fileId,
                includeDependencies: !_commandLine.HasFlag("no-dependencies"))
            .ConfigureAwait(false);
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

        Console.WriteLine($"已安装 {report.ModName}（项目 {report.ProjectId}，文件 {report.FileId}）");
        foreach (var file in report.InstalledFiles)
        {
            Console.WriteLine($"  + {file}");
        }

        foreach (var warning in report.Warnings)
        {
            Console.WriteLine($"  ! {warning}");
        }

        return 0;
    }

    private static async Task<Result<CurseForgeMod>> ResolveCurseForgeModAsync(
        ICurseForgeClient client,
        string reference)
    {
        if (int.TryParse(reference, out var modId) && modId > 0)
        {
            return await client.GetModAsync(modId).ConfigureAwait(false);
        }

        var bySlug = await client.FindModBySlugAsync(reference).ConfigureAwait(false);
        if (!bySlug.IsSuccess)
        {
            return Result<CurseForgeMod>.Fail(bySlug.Error!);
        }

        return bySlug.Value is null
            ? Result<CurseForgeMod>.Fail("curseforge.mod_not_found", $"没有找到 CurseForge 项目：{reference}")
            : Result<CurseForgeMod>.Ok(bySlug.Value);
    }

    private bool TryGetCurseForgeLoader(out CurseForgeModLoader? loader)
    {
        loader = null;
        var text = _commandLine.GetValue("loader");
        if (string.IsNullOrWhiteSpace(text) || string.Equals(text, "any", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!CurseForgeLoaders.TryParse(text, out var parsed))
        {
            return false;
        }

        loader = parsed;
        return true;
    }

    private int GetIntOption(string name, int fallback) =>
        int.TryParse(_commandLine.GetValue(name), out var value) ? value : fallback;

    private static string FormatCurseForgeReleaseType(int releaseType) => releaseType switch
    {
        1 => "Release",
        2 => "Beta",
        3 => "Alpha",
        _ => "Unknown",
    };

    private async Task<int> PackCommandAsync(string[] args)
    {
        if (args.Length < 2)
        {
            return UsageError("pack 支持 list、enable、disable、import、delete。");
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

            var list = result.Value!;
            if (_commandLine.HasFlag("json"))
            {
                Console.WriteLine(JsonSerializer.Serialize(list, EclJson.Indented));
                return 0;
            }

            if (list.Packs.Count == 0)
            {
                Console.WriteLine("没有找到资源包/光影包。");
            }
            else
            {
                Console.WriteLine($"{list.EnabledCount} 启用 / {list.Packs.Count} 个");
                foreach (var entry in list.Packs)
                {
                    Console.WriteLine(
                        $"  [{(entry.Enabled ? "启用" : "禁用")}] {entry.FileName}" +
                        (entry.Incompatible ? "（已确认不兼容）" : string.Empty));
                    Console.WriteLine($"         name: {entry.Name}");
                    if (entry.PackFormat is not null)
                    {
                        Console.WriteLine($"         pack format: {entry.PackFormat}");
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(list.Warning))
            {
                Console.WriteLine($"! {list.Warning}");
            }

            foreach (var entry in list.MissingEntries)
            {
                Console.WriteLine($"! 已启用但文件缺失：{entry}");
            }

            foreach (var entry in list.UnmanagedEntries)
            {
                Console.WriteLine($"! options.txt 中的未识别条目：{entry}");
            }

            foreach (var entry in list.DuplicateEntries)
            {
                Console.WriteLine($"! options.txt 中的重复条目：{entry}");
            }

            return 0;
        }

        if (subCommand is "enable" or "disable")
        {
            var fileName = args.ElementAtOrDefault(2);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return UsageError($"pack {subCommand} 需要文件名。");
            }

            var enabled = subCommand == "enable";
            var result = await service
                .SetEnabledAsync(instanceId, kind, fileName, enabled)
                .ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                return WriteError(result.Error);
            }

            Console.WriteLine($"已{(enabled ? "启用" : "禁用")} {fileName}");
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

    private async Task<int> CrashCommandAsync(string[] args)
    {
        var instanceId = args.ElementAtOrDefault(0);
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return UsageError("crash 需要实例 ID。");
        }

        var result = await _provider.GetRequiredService<ICrashReportService>()
            .AnalyzeLatestAsync(instanceId)
            .ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            return WriteError(result.Error);
        }

        if (result.Value is null)
        {
            Console.WriteLine("未找到崩溃报告或 latest.log 中的异常。");
            return 0;
        }

        var analysis = result.Value;
        if (_commandLine.HasFlag("json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(analysis, EclJson.Indented));
            return 0;
        }

        Console.WriteLine(analysis.Summary);
        if (!string.IsNullOrWhiteSpace(analysis.SourcePath))
        {
            Console.WriteLine($"Source: {analysis.SourcePath}");
        }

        foreach (var suspect in analysis.Suspects)
        {
            Console.WriteLine(
                $"  ? {suspect.Name ?? suspect.ModId} ({suspect.ModId}) score={suspect.Score}");
            foreach (var evidence in suspect.Evidence)
            {
                Console.WriteLine($"    {evidence}");
            }
        }

        return 0;
    }

    private async Task<int> DiagnosticsCommandAsync(string[] args)
    {
        if (args.Length < 2 || !string.Equals(args[0], "export", StringComparison.OrdinalIgnoreCase))
        {
            return UsageError("diagnostics 支持 export <实例ID> [--output <路径>]。");
        }

        var instanceId = args[1];
        var output = _commandLine.GetValue("output");
        var result = await _provider.GetRequiredService<IDiagnosticPackageService>()
            .ExportAsync(instanceId, output)
            .ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            return WriteError(result.Error);
        }

        if (_commandLine.HasFlag("json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(result.Value, EclJson.Indented));
            return 0;
        }

        Console.WriteLine($"已导出诊断包：{result.Value!.OutputPath}");
        Console.WriteLine($"  Entries: {result.Value.Entries}");
        Console.WriteLine($"  Bytes  : {result.Value.Bytes}");
        return 0;
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

    private static Task OpenBrowserAsync(Uri uri)
    {
        Console.WriteLine($"正在打开系统浏览器：{uri.GetLeftPart(UriPartial.Path)}");
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        return Task.CompletedTask;
    }

    /// <summary>
    /// 读取密码：交互式终端下不回显；输入被重定向时按一行读取。
    /// 密码只用于本次认证调用，不会写入日志或磁盘。
    /// </summary>
    private static string ReadPassword(string prompt)
    {
        Console.Write(prompt);
        if (Console.IsInputRedirected)
        {
            var piped = Console.ReadLine();
            Console.WriteLine();
            return piped ?? string.Empty;
        }

        var buffer = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return buffer.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (buffer.Length > 0)
                {
                    buffer.Length--;
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                buffer.Append(key.KeyChar);
            }
        }
    }

    /// <summary>
    /// 工作区命令：选择、校验、建议、旧布局沿用。所有会写入工作区的命令都必须先确认
    /// 工作区（ADR 0018），避免在用户不知情时把实例写进数据目录。
    /// </summary>
    private async Task<int> WorkspaceCommandAsync(string[] args)
    {
        var workspace = _provider.GetRequiredService<IWorkspaceService>();
        var paths = _provider.GetRequiredService<IAppPaths>();
        var json = _commandLine.HasFlag("json");
        var subCommand = args.Length == 0 ? "status" : args[0].ToLowerInvariant();

        switch (subCommand)
        {
            case "status":
                {
                    var primary = workspace.Primary;
                    WorkspacePathCheck? check = null;
                    if (primary is not null)
                    {
                        var validation = await workspace.ValidatePrimaryAsync().ConfigureAwait(false);
                        check = validation.Value;
                    }

                    if (json)
                    {
                        WriteJson(new
                        {
                            configured = workspace.IsConfigured,
                            primary,
                            check,
                            roots = workspace.Roots,
                            storage = workspace.GetStorageLayout(),
                        });
                        return 0;
                    }

                    Console.WriteLine($"已配置      : {(workspace.IsConfigured ? "是" : "否")}");
                    Console.WriteLine($"工作区      : {primary?.Path ?? "（未设置）"}");
                    if (check is not null)
                    {
                        Console.WriteLine($"状态        : {check.Status}");
                        if (check.FreeBytes is { } free)
                        {
                            Console.WriteLine($"可用空间    : {FormatBytes(free)}");
                        }

                        if (check.ProblemMessage is not null)
                        {
                            Console.WriteLine($"问题        : {check.ProblemMessage}");
                        }

                        foreach (var warning in check.WarningCodes)
                        {
                            Console.WriteLine($"警告        : {warning}");
                        }
                    }

                    Console.WriteLine($"数据目录    : {paths.Root}");
                    Console.WriteLine($"运行时目录  : {paths.RuntimesDirectory}");
                    Console.WriteLine($"已登记根目录: {workspace.Roots.Count}");
                    return 0;
                }

            case "set":
                {
                    var path = args.ElementAtOrDefault(1) ?? _commandLine.GetValue("path");
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        return UsageError("workspace set 需要目标目录。");
                    }

                    var result = await workspace.InitializeAsync(path).ConfigureAwait(false);
                    if (!result.IsSuccess)
                    {
                        return WriteError(result.Error);
                    }

                    var root = result.Value!;
                    var check = workspace.Check(root.Path, createIfMissing: false);
                    if (json)
                    {
                        WriteJson(new { workspace = root, check });
                        return 0;
                    }

                    Console.WriteLine($"已设置 Minecraft 工作区：{root.Path}");
                    Console.WriteLine("之后创建实例、安装游戏与下载内容都会写入该目录。");
                    PrintWarnings(check);
                    return 0;
                }

            case "validate":
                {
                    var result = await workspace.ValidatePrimaryAsync().ConfigureAwait(false);
                    if (!result.IsSuccess)
                    {
                        return WriteError(result.Error);
                    }

                    var check = result.Value!;
                    if (json)
                    {
                        WriteJson(check);
                        return check.IsUsable ? 0 : 1;
                    }

                    Console.WriteLine($"路径  : {check.Path}");
                    Console.WriteLine($"状态  : {check.Status}");
                    Console.WriteLine($"可用  : {(check.IsUsable ? "是" : "否")}");
                    if (check.ProblemMessage is not null)
                    {
                        Console.WriteLine($"问题  : {check.ProblemMessage}");
                    }

                    PrintWarnings(check);
                    return check.IsUsable ? 0 : 1;
                }

            case "suggest":
                {
                    var suggestions = workspace.Suggest();
                    if (json)
                    {
                        WriteJson(suggestions);
                        return 0;
                    }

                    if (suggestions.Count == 0)
                    {
                        Console.WriteLine("没有可自动推荐的目录，请显式指定工作区路径。");
                        return 0;
                    }

                    foreach (var suggestion in suggestions)
                    {
                        Console.WriteLine($"{(suggestion.IsRecommended ? "*" : " ")} {suggestion.Path}  ({suggestion.ReasonCode})");
                    }

                    return 0;
                }

            case "adopt-legacy":
                {
                    var result = await workspace.AdoptLegacyAsync().ConfigureAwait(false);
                    if (!result.IsSuccess)
                    {
                        return WriteError(result.Error);
                    }

                    Console.WriteLine($"已继续使用旧版数据目录：{result.Value!.Path}");
                    Console.WriteLine("该位置位于启动器数据目录中，建议稍后迁移到用户可见的数据盘。");
                    return 0;
                }

            case "skip":
                {
                    await workspace.SkipOnboardingAsync().ConfigureAwait(false);
                    Console.WriteLine("已跳过工作区配置。创建实例或下载前仍会要求确认工作区。");
                    return 0;
                }

            case "migrate":
                return await WorkspaceMigrateCommandAsync(workspace).ConfigureAwait(false);

            case "roots":
                {
                    if (json)
                    {
                        WriteJson(workspace.Roots);
                        return 0;
                    }

                    foreach (var root in workspace.Roots)
                    {
                        Console.WriteLine($"{root.Id}  {root.Kind,-8}  {root.Status,-10}  {root.Path}");
                    }

                    return 0;
                }

            default:
                return UsageError("workspace 支持 status、set、validate、suggest、adopt-legacy、skip、roots。");
        }
    }

    /// <summary>
    /// 工作区迁移：默认只显示计划；--apply 执行复制→校验→切换。--resume/--discard 处理中断。
    /// 源目录始终保留，ECL 不会自动删除。
    /// </summary>
    private async Task<int> WorkspaceMigrateCommandAsync(IWorkspaceService workspace)
    {
        var migration = _provider.GetRequiredService<IWorkspaceMigrationService>();
        var json = _commandLine.HasFlag("json");

        if (_commandLine.HasFlag("resume"))
        {
            var resumed = await migration.ResumeAsync().ConfigureAwait(false);
            if (!resumed.IsSuccess)
            {
                return WriteError(resumed.Error);
            }

            return PrintMigration(resumed.Value!, json);
        }

        if (_commandLine.HasFlag("discard"))
        {
            var discarded = await migration.DiscardAsync().ConfigureAwait(false);
            if (!discarded.IsSuccess)
            {
                return WriteError(discarded.Error);
            }

            Console.WriteLine("已清理迁移 staging；源工作区未受影响。");
            return 0;
        }

        if (_commandLine.HasFlag("status"))
        {
            if (migration.Current is null)
            {
                Console.WriteLine("没有迁移记录。");
                return 0;
            }

            return PrintMigration(migration.Current, json);
        }

        var target = _commandLine.GetValue("to");
        if (string.IsNullOrWhiteSpace(target))
        {
            return UsageError("workspace migrate 需要 --to <目录>，或使用 --resume / --discard / --status。");
        }

        var planResult = await migration.PlanAsync(target).ConfigureAwait(false);
        if (!planResult.IsSuccess)
        {
            return WriteError(planResult.Error);
        }

        var plan = planResult.Value!;
        if (!_commandLine.HasFlag("apply"))
        {
            if (json)
            {
                WriteJson(plan);
                return plan.CanProceed ? 0 : 1;
            }

            Console.WriteLine($"源工作区 : {plan.SourcePath}");
            Console.WriteLine($"目标位置 : {plan.TargetPath}");
            Console.WriteLine($"实例数量 : {plan.InstanceCount}");
            Console.WriteLine($"文件数量 : {plan.TotalFiles}");
            Console.WriteLine($"数据大小 : {FormatBytes(plan.TotalBytes)}");
            if (plan.FreeBytes is { } free)
            {
                Console.WriteLine($"目标可用 : {FormatBytes(free)}");
            }

            foreach (var problem in plan.Problems)
            {
                Console.WriteLine($"阻塞     : {problem}");
            }

            foreach (var warning in plan.Warnings)
            {
                Console.WriteLine($"警告     : {_provider.GetRequiredService<ILocalizer>().Get(warning)}");
            }

            Console.WriteLine(plan.CanProceed
                ? "计划可执行。加 --apply 开始迁移（复制→校验→切换，源目录保留）。"
                : "当前条件不满足，未执行迁移。");
            return plan.CanProceed ? 0 : 1;
        }

        if (!plan.CanProceed)
        {
            return WriteError(new EclError(
                "workspace.migration.blocked",
                plan.Problems.FirstOrDefault() ?? "迁移条件不满足。"));
        }

        var started = await migration.StartAsync(target).ConfigureAwait(false);
        if (!started.IsSuccess)
        {
            return WriteError(started.Error);
        }

        return PrintMigration(started.Value!, json);
    }

    private int PrintMigration(WorkspaceMigrationRecord record, bool json)
    {
        if (json)
        {
            WriteJson(record);
            return record.State == WorkspaceMigrationState.Completed ? 0 : 1;
        }

        Console.WriteLine($"迁移状态 : {record.State}");
        Console.WriteLine($"源工作区 : {record.SourcePath}");
        Console.WriteLine($"目标位置 : {record.TargetPath}");
        Console.WriteLine($"文件数量 : {record.CopiedFiles}/{record.TotalFiles}");
        if (record.ErrorMessage is not null)
        {
            Console.WriteLine($"错误     : {record.ErrorMessage}");
        }

        if (record.State == WorkspaceMigrationState.Completed)
        {
            Console.WriteLine("迁移完成；源目录仍保留，可在确认后自行清理。");
            return 0;
        }

        Console.WriteLine("迁移未完成。可运行 workspace migrate --resume 或 --discard。");
        return 1;
    }

    /// <summary>
    /// 会话命令：查看、导出与恢复。会话按进程启动划分，不等同于实例或 latest.log。
    /// </summary>
    private async Task<int> SessionCommandAsync(string[] args)
    {
        var sessions = _provider.GetRequiredService<IGameSessionService>();
        var exporter = _provider.GetRequiredService<ISessionExportService>();
        var json = _commandLine.HasFlag("json");
        var subCommand = args.Length == 0 ? "list" : args[0].ToLowerInvariant();

        switch (subCommand)
        {
            case "list":
                {
                    var list = sessions.List();
                    if (json)
                    {
                        WriteJson(list.Select(session => new
                        {
                            sessionId = session.SessionId,
                            state = session.Manifest.State.ToString(),
                            instanceId = session.Manifest.InstanceId,
                            instanceName = session.Manifest.InstanceName,
                            createdAtUtc = session.Manifest.CreatedAtUtc,
                            processId = session.Manifest.ProcessId,
                            logIsolation = session.Manifest.LogIsolation,
                        }));
                        return 0;
                    }

                    if (list.Count == 0)
                    {
                        Console.WriteLine("没有游戏日志会话。");
                        return 0;
                    }

                    foreach (var session in list)
                    {
                        Console.WriteLine(
                            $"{session.SessionId}  {session.Manifest.State,-20} {session.Manifest.InstanceName ?? "-"}  " +
                            $"{session.Manifest.CreatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
                    }

                    return 0;
                }

            case "show":
                {
                    var sessionId = args.ElementAtOrDefault(1);
                    if (string.IsNullOrWhiteSpace(sessionId))
                    {
                        return UsageError("session show 需要会话 ID。");
                    }

                    var session = sessions.Get(sessionId);
                    if (session is null)
                    {
                        return WriteError(new EclError("session.not_found", $"会话不存在：{sessionId}"));
                    }

                    var events = SessionJournal.ReadAll(session.JournalPath, out var torn);
                    if (json)
                    {
                        WriteJson(new { session = session.Manifest, events, torn });
                        return 0;
                    }

                    Console.WriteLine($"会话      : {session.SessionId}");
                    Console.WriteLine($"实例      : {session.Manifest.InstanceName ?? "-"} ({session.Manifest.InstanceId ?? "-"})");
                    Console.WriteLine($"状态      : {session.Manifest.State}");
                    Console.WriteLine($"创建      : {session.Manifest.CreatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
                    Console.WriteLine($"进程      : {session.Manifest.ProcessId?.ToString() ?? "未知"}");
                    Console.WriteLine($"日志隔离  : {session.Manifest.LogIsolation}");
                    Console.WriteLine($"证据      : {session.Manifest.Artifacts.Count} 项");
                    foreach (var artifact in session.Manifest.Artifacts)
                    {
                        Console.WriteLine($"  - {artifact.Kind} [{artifact.Ownership}] {artifact.Warning}");
                    }

                    foreach (var warning in session.Manifest.Warnings)
                    {
                        Console.WriteLine($"警告      : {warning}");
                    }

                    if (torn)
                    {
                        Console.WriteLine("注意      : journal 最后一条记录不完整，已忽略。");
                    }

                    if (events.Count > 0)
                    {
                        Console.WriteLine($"事件      : {events.Count} 条，最后 {events[^1].Type}");
                    }

                    return 0;
                }

            case "export":
                {
                    var sessionId = args.ElementAtOrDefault(1);
                    if (string.IsNullOrWhiteSpace(sessionId))
                    {
                        return UsageError("session export 需要会话 ID。");
                    }

                    using var operations = TrackOperations();
                    var result = await exporter
                        .ExportAsync(sessionId, _commandLine.GetValue("output"))
                        .ConfigureAwait(false);
                    if (!result.IsSuccess)
                    {
                        return WriteError(result.Error);
                    }

                    var report = result.Value!;
                    if (json)
                    {
                        WriteJson(report);
                        return 0;
                    }

                    Console.WriteLine($"已导出会话 {sessionId}：{report.OutputPath}（{report.Bytes} 字节）");
                    if (report.Missing.Count > 0)
                    {
                        Console.WriteLine("缺失或无法关联：");
                        foreach (var missing in report.Missing)
                        {
                            Console.WriteLine($"  - {missing}");
                        }
                    }

                    return 0;
                }

            case "prune":
                {
                    if (!int.TryParse(_commandLine.GetValue("older-than-days"), out var days) || days <= 0)
                    {
                        return UsageError("session prune 需要 --older-than-days <正整数>。");
                    }

                    var retention = _provider.GetRequiredService<ISessionRetentionService>();
                    var plan = retention.Plan(TimeSpan.FromDays(days));
                    if (json)
                    {
                        WriteJson(plan);
                        return 0;
                    }

                    Console.WriteLine($"可清理会话：{plan.SessionIds.Count}，约 {plan.Bytes} 字节");
                    foreach (var id in plan.SessionIds)
                    {
                        Console.WriteLine($"  - {id}");
                    }

                    foreach (var reason in plan.ProtectedReasons)
                    {
                        Console.WriteLine($"  ! {reason}");
                    }

                    if (!_commandLine.HasFlag("apply"))
                    {
                        Console.WriteLine("加 --apply 才会真正删除；运行中或状态未知的会话不会被清理。");
                        return 0;
                    }

                    var pruned = retention.Apply(plan);
                    return pruned.IsSuccess ? 0 : WriteError(pruned.Error);
                }

            case "recover":
                {
                    var report = sessions.Recover();
                    if (json)
                    {
                        WriteJson(report);
                        return 0;
                    }

                    Console.WriteLine($"扫描 {report.Scanned}，中断 {report.Interrupted}，重连 {report.Reattached}，未知 {report.Unknown}。");
                    foreach (var warning in report.Warnings)
                    {
                        Console.WriteLine($"  ! {warning}");
                    }

                    return 0;
                }

            default:
                return UsageError("session 支持 list、show、export、recover 与 prune。");
        }
    }

    /// <summary>会写入工作区的命令必须先通过该检查；未配置时给出可执行的第一步。</summary>
    private async Task<int?> RequireManagedWorkspaceAsync()
    {
        var workspace = _provider.GetRequiredService<IWorkspaceService>();
        if (!workspace.IsConfigured)
        {
            Console.Error.WriteLine("[workspace.not_configured] 尚未确认 Minecraft 工作区。");
            Console.Error.WriteLine("请先运行：ecl workspace set <目录>");
            return 1;
        }

        var result = await workspace.ValidatePrimaryAsync().ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return WriteError(result.Error);
        }

        var check = result.Value!;
        if (!check.IsUsable)
        {
            Console.Error.WriteLine($"[{check.ProblemCode ?? "workspace.unavailable"}] {check.ProblemMessage}");
            Console.Error.WriteLine("请运行 ecl workspace set <目录> 重新定位工作区。");
            return 1;
        }

        return null;
    }

    private static void WriteJson<T>(T value) =>
        Console.WriteLine(JsonSerializer.Serialize(value, EclJson.Indented));

    private static void PrintWarnings(WorkspacePathCheck check)
    {
        foreach (var warning in check.WarningCodes)
        {
            Console.WriteLine($"警告：{warning}");
        }
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024 * 1024):F1} GB",
        >= 1024L * 1024 => $"{bytes / (1024d * 1024):F1} MB",
        _ => $"{bytes} B",
    };

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

    /// <summary>
    /// 订阅任务中心，让安装、整合包、Java 与 CurseForge 等长任务输出统一的
    /// 阶段、百分比、速度与失败归因；进度写标准错误，避免污染 <c>--json</c>。
    /// </summary>
    private CliOperationReporter TrackOperations() =>
        new(_provider.GetRequiredService<IOperationStore>(), _provider.GetRequiredService<ILocalizer>());
}
