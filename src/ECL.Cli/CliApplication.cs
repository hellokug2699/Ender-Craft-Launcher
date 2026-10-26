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
using Ecl.Minecraft.Install;
using Ecl.Minecraft.Instances;
using Ecl.Minecraft.Launch;
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
                    var result = await instances.CreateAsync(name, version, loader, loaderVersion).ConfigureAwait(false);
                    if (!result.IsSuccess)
                    {
                        return WriteError(result.Error);
                    }

                    var instance = result.Value!;
                    Console.WriteLine($"已创建实例 {instance.Name}");
                    Console.WriteLine($"  ID      : {instance.Id}");
                    Console.WriteLine($"  Version : {instance.VersionId}");
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

        if (!version.IsSuccess)
        {
            return WriteError(version.Error);
        }

        var requirement = JavaRequirements.Resolve(version.Value!, instance.GameVersion);
        var localizer = _provider.GetRequiredService<ILocalizer>();
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
