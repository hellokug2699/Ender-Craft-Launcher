// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using Ecl.Core.Localization;
using Microsoft.Extensions.DependencyInjection;

namespace Ecl.Core;

public static class EclCoreServiceCollectionExtensions
{
    public static IServiceCollection AddEclCore(this IServiceCollection services, AppPaths paths, CultureInfo? culture = null)
    {
        services.AddSingleton(paths);
        services.AddSingleton<IAppPaths>(paths);
        services.AddSingleton<ILocalizer>(_ => new JsonLocalizer(culture));
        return services;
    }
}
