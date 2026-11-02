// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Configuration;
using Ecl.Core;
using Ecl.Java;
using Ecl.Minecraft.Content;
using Ecl.Minecraft.CurseForge;
using Ecl.Minecraft.Diagnostics;
using Ecl.Minecraft.Eula;
using Ecl.Minecraft.Install;
using Ecl.Minecraft.Instances;
using Ecl.Minecraft.Java;
using Ecl.Minecraft.Launch;
using Ecl.Minecraft.Launch.Steps;
using Ecl.Minecraft.Loaders;
using Ecl.Minecraft.Modrinth;
using Ecl.Minecraft.Mods;
using Ecl.Minecraft.News;
using Ecl.Minecraft.Storage;
using Ecl.Minecraft.Versions;
using Ecl.Minecraft.World;
using Ecl.Sessions;
using Ecl.World.Diagnostics;
using Ecl.World.Recent;
using Ecl.World.Servers;
using Microsoft.Extensions.DependencyInjection;

namespace Ecl.Minecraft;

public static class MinecraftServiceCollectionExtensions
{
    public static IServiceCollection AddEclMinecraft(this IServiceCollection services)
    {
        services.AddEclSessions();
        services.AddSingleton<IOptionProvider, MinecraftOptionsProvider>();
        services.AddSingleton<IJavaSearchRootProvider, SettingsJavaSearchRootProvider>();
        services.AddSingleton<IInstanceStore, FileSystemInstanceStore>();
        services.AddSingleton<IInstanceService, InstanceService>();
        services.AddSingleton<IVersionResolver, VersionResolver>();
        services.AddSingleton<IVersionCatalog, MojangVersionCatalog>();
        services.AddSingleton<SystemProcessLauncher>();
        services.AddSingleton<LogHostProcessLauncher>();
        services.AddSingleton<IProcessLauncher, HybridProcessLauncher>();
        services.AddSingleton<ILoaderCatalog, LoaderCatalog>();
        services.AddSingleton<IForgeProcessorRunner, ForgeProcessorRunner>();
        services.AddSingleton<IVersionInstallService, VanillaInstallService>();
        services.AddSingleton<IJavaSelectionService, JavaSelectionService>();
        services.AddSingleton<IJavaRequirementProvider, ModJavaRequirementProvider>();
        services.AddSingleton<IEulaService, FileEulaService>();
        services.AddSingleton<IModService, ModService>();
        services.AddSingleton<IModDependencyInstaller, ModDependencyInstaller>();
        services.AddSingleton<IModpackService, ModpackService>();
        services.AddSingleton<ICurseForgeClient, CurseForgeClient>();
        services.AddSingleton<ICurseForgeService, CurseForgeService>();
        services.AddSingleton<IModrinthClient, ModrinthClient>();
        services.AddSingleton<IContentSearchService, ContentSearchService>();
        services.AddSingleton<IDependencyResolver, DependencyResolver>();
        services.AddSingleton<IContentInstallService, ContentInstallService>();
        services.AddSingleton<IContentPackService, ContentPackService>();
        services.AddSingleton<IVersionNewsService, MojangVersionNewsService>();
        services.AddSingleton<ICrashReportService, CrashReportService>();
        services.AddSingleton<IDiagnosticPackageService, DiagnosticPackageService>();
        services.AddSingleton<StorageAnalysisService>();
        services.AddSingleton<StorageCleanupService>();
        services.AddSingleton<ServerPingService>();
        services.AddSingleton<WorldDiagnosticsParser>();
        services.AddSingleton(provider => new RecentAccessStore(
            Path.Combine(provider.GetRequiredService<IAppPaths>().ConfigDirectory, "recent-access.json")));
        services.AddSingleton(provider => new DiagnosticReportStore(
            Path.Combine(provider.GetRequiredService<IAppPaths>().ConfigDirectory, "diagnostics")));
        services.AddSingleton<WorldWorkbenchService>();

        services.AddSingleton<ILaunchStep, ValidateInstanceStep>();
        services.AddSingleton<ILaunchStep, ResolveJavaStep>();
        services.AddSingleton<ILaunchStep, ResolveVersionStep>();
        services.AddSingleton<ILaunchStep, ValidateGameFilesStep>();
        services.AddSingleton<ILaunchStep, ExtractNativesStep>();
        services.AddSingleton<ILaunchStep, EnsureEulaStep>();
        services.AddSingleton<IAuthlibInjectorService, AuthlibInjectorService>();
        services.AddSingleton<ILaunchStep, AuthlibInjectorStep>();
        services.AddSingleton<ILaunchStep, BuildCommandStep>();
        services.AddSingleton<ILaunchStep, StartGameStep>();
        services.AddSingleton<ILaunchService, LaunchService>();

        return services;
    }
}
