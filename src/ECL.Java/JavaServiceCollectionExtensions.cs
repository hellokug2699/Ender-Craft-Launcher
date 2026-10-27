// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.Extensions.DependencyInjection;

namespace Ecl.Java;

public static class JavaServiceCollectionExtensions
{
    public static IServiceCollection AddEclJava(this IServiceCollection services)
    {
        services.AddSingleton<IJavaLocator, JavaLocator>();
        services.AddSingleton<IJavaInstallService, JavaInstallService>();
        services.AddSingleton<IJavaRuntimeProvider, AdoptiumJavaRuntimeProvider>();
        services.AddSingleton<IJavaRuntimeProvider, ZuluJavaRuntimeProvider>();
        services.AddSingleton<IJavaRuntimeProvider, LibericaJavaRuntimeProvider>();
        return services;
    }
}
