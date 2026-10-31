// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Auth;
using Ecl.Configuration.Settings;
using Ecl.Core;
using Ecl.Java;
using Ecl.Minecraft.Instances;
using Ecl.Minecraft.Versions;

namespace Ecl.Minecraft.Launch;

public sealed class LaunchContext
{
    public required GameInstance Instance { get; init; }

    public required Account Account { get; init; }

    public required IAppPaths Paths { get; init; }

    public required ISettingsService Settings { get; init; }

    public required bool DryRun { get; init; }

    public JavaRuntime? Java { get; set; }

    public MinecraftVersion? Version { get; set; }

    public int MemoryMb { get; set; } = 4096;

    public string JavaExecutable { get; set; } = "java";

    public string? JvmPresetWarning { get; set; }

    /// <summary>
    /// Authlib-Injector 注入参数（仅第三方账户设置），形如
    /// <c>-javaagent:&lt;jar&gt;=&lt;authserver&gt;</c>。
    /// </summary>
    public string? JavaAgentArgument { get; set; }

    public LaunchCommand? Command { get; set; }

    public int? ProcessId { get; set; }

    public string InstanceDirectory => Paths.GetInstanceDirectory(Instance.Id);

    public string GameDirectory => InstancePaths.GetGameDirectory(Paths, Instance);

    public string NativesDirectory => InstancePaths.GetNativesDirectory(Paths, Instance);

    public string VersionDirectory => Path.Combine(Paths.VersionsDirectory, Instance.VersionId);

    public T? GetInstanceSetting<T>(string key) => Settings.Get<T>(key, Instance.Id);
}

public sealed record LaunchCommand(string Executable, IReadOnlyList<string> Arguments, string Display);
