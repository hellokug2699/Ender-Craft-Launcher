// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Configuration;
using Ecl.Minecraft.Diagnostics;
using Ecl.Minecraft.Eula;
using Ecl.Minecraft.Install;
using Ecl.Minecraft.Instances;
using Ecl.Minecraft.Java;
using Ecl.Minecraft.Launch;
using Ecl.Minecraft.Launch.Steps;
using Ecl.Minecraft.Loaders;
using Ecl.Minecraft.Mods;
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
        services.AddSingleton<ILoaderCatalog, LoaderCatalog>();
        services.AddSingleton<IForgeProcessorRunner, ForgeProcessorRunner>();
        services.AddSingleton<IVersionInstallService, VanillaInstallService>();
        services.AddSingleton<IJavaSelectionService, JavaSelectionService>();
        services.AddSingleton<IEulaService, FileEulaService>();
        services.AddSingleton<IModService, ModService>();
        services.AddSingleton<IModpackService, ModpackService>();
        services.AddSingleton<IContentPackService, ContentPackService>();
        services.AddSingleton<ICrashReportService, CrashReportService>();
        services.AddSingleton<IDiagnosticPackageService, DiagnosticPackageService>();

        services.AddSingleton<ILaunchStep, ValidateInstanceStep>();
        services.AddSingleton<ILaunchStep, ResolveJavaStep>();
        services.AddSingleton<ILaunchStep, ResolveVersionStep>();
        services.AddSingleton<ILaunchStep, ValidateGameFilesStep>();
        services.AddSingleton<ILaunchStep, ExtractNativesStep>();
        services.AddSingleton<ILaunchStep, EnsureEulaStep>();
        services.AddSingleton<ILaunchStep, BuildCommandStep>();
        services.AddSingleton<ILaunchStep, StartGameStep>();
        services.AddSingleton<ILaunchService, LaunchService>();

        return services;
    }
}
