// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using Ecl.Core.Localization;
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
        services.AddSingleton<ILocalizer>(_ => new JsonLocalizer(culture));
        return services;
    }
}
