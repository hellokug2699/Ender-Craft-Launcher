// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using Ecl.Core.Diagnostics;
using Ecl.Core.Localization;
using Ecl.Core.Runtime;
using Ecl.Core.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ecl.Core;

public static class EclCoreServiceCollectionExtensions
{
    public static IServiceCollection AddEclCore(this IServiceCollection services, AppPaths paths, CultureInfo? culture = null)
    {
        services.AddSingleton(paths);
        services.AddSingleton<IAppPaths>(paths);
        services.AddSingleton<IWorkspaceService>(provider => new WorkspaceService(
            paths,
            provider.GetService<ILogger<WorkspaceService>>() ?? NullLogger<WorkspaceService>.Instance));
        services.AddSingleton<IWorkspaceMigrationService>(provider => new WorkspaceMigrationService(
            paths,
            provider.GetRequiredService<IWorkspaceService>(),
            provider.GetService<ILogger<WorkspaceMigrationService>>() ?? NullLogger<WorkspaceMigrationService>.Instance));
        services.AddSingleton<InstanceSourceService>();
        services.AddSingleton<ILocalizer>(_ => new JsonLocalizer(culture));

        // 顶部内存压力监控与启动服务的自动内存策略共用同一份系统采样。
        services.AddSingleton<ISystemMemoryProbe, SystemMemoryProbe>();

        // ADR 0039：应用级生命周期协调器。App/CLI 各自在组合根解析并驱动状态。
        services.AddSingleton<ApplicationLifecycleCoordinator>();
        services.AddSingleton<IApplicationLifecycle>(provider => provider.GetRequiredService<ApplicationLifecycleCoordinator>());
        return services;
    }
}
