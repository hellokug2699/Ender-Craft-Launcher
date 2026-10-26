// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Configuration.Settings;
using Ecl.Core.Localization;
using Ecl.Java;

namespace Ecl.Minecraft.Launch.Steps;

internal sealed class ResolveJavaStep(IJavaLocator javaLocator, ILocalizer localizer) : ILaunchStep
{
    public string Id => "java";

    public int Order => 200;

    public async Task<LaunchStepResult> ExecuteAsync(LaunchContext context, CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        var instanceJavaPath = context.GetInstanceSetting<string?>("instance.java.executablePath");
        var globalJavaPath = context.Settings.Get<string>("java.defaultPath");

        JavaRuntime? runtime = null;
        foreach (var configuredPath in new[] { instanceJavaPath, globalJavaPath })
        {
            if (string.IsNullOrWhiteSpace(configuredPath))
            {
                continue;
            }

            runtime = javaLocator.FromPath(configuredPath);
            if (runtime is null)
            {
                warnings.Add(localizer.Get("launch.java.configuredInvalid", configuredPath));
                continue;
            }

            break;
        }

        runtime ??= (await javaLocator.FindAllAsync(cancellationToken).ConfigureAwait(false))
            .OrderByDescending(item => item.Version)
            .FirstOrDefault();

        var instanceMemory = context.GetInstanceSetting<int?>("instance.java.memoryMb");
        var globalMemory = context.Settings.Get<int>("java.defaultMemoryMb");
        context.MemoryMb = Math.Clamp(instanceMemory ?? globalMemory, 1024, 65536);

        if (runtime is null)
        {
            context.JavaExecutable = "java";
            var message = localizer.Get(
                context.DryRun ? "launch.java.notFound.preview" : "launch.java.notFound",
                context.MemoryMb);
            if (warnings.Count > 0)
            {
                message = $"{message} {string.Join(' ', warnings)}";
            }

            return new LaunchStepResult(
                Id,
                context.DryRun ? LaunchStepStatus.Warning : LaunchStepStatus.Failed,
                message);
        }

        context.Java = runtime;
        context.JavaExecutable = runtime.ExecutablePath;

        var foundMessage = localizer.Get("launch.java.found", runtime.DisplayName, context.MemoryMb);
        if (warnings.Count > 0)
        {
            foundMessage = $"{foundMessage} {string.Join(' ', warnings)}";
        }

        return new LaunchStepResult(
            Id,
            warnings.Count > 0 ? LaunchStepStatus.Warning : LaunchStepStatus.Success,
            foundMessage);
    }
}
