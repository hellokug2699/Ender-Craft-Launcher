// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Configuration;
using Ecl.Minecraft.Instances;
using Ecl.Minecraft.Launch;
using Ecl.Minecraft.Launch.Steps;
using Ecl.Minecraft.Versions;
using Microsoft.Extensions.DependencyInjection;

namespace Ecl.Minecraft;

public static class MinecraftServiceCollectionExtensions
{
    public static IServiceCollection AddEclMinecraft(this IServiceCollection services)
    {
        services.AddSingleton<IOptionProvider, MinecraftOptionsProvider>();
        services.AddSingleton<IInstanceStore, FileSystemInstanceStore>();
        services.AddSingleton<IInstanceService, InstanceService>();
        services.AddSingleton<IVersionResolver, VersionResolver>();
        services.AddSingleton<IProcessLauncher, SystemProcessLauncher>();

        services.AddSingleton<ILaunchStep, ValidateInstanceStep>();
        services.AddSingleton<ILaunchStep, ResolveJavaStep>();
        services.AddSingleton<ILaunchStep, ResolveVersionStep>();
        services.AddSingleton<ILaunchStep, BuildCommandStep>();
        services.AddSingleton<ILaunchStep, StartGameStep>();
        services.AddSingleton<ILaunchService, LaunchService>();

        return services;
    }
}
