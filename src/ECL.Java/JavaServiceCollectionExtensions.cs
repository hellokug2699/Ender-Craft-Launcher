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

        // Java 管理页（设置中心）使用的运行时清单、登记、验证与卸载能力。
        // 验证通过真实进程执行，因此注册为单例以复用探测入口；页面只在首次进入时触发扫描/验证。
        services.AddSingleton<IJavaRuntimeRegistry, JavaRuntimeRegistry>();
        services.AddSingleton<IJavaVersionProbe, ProcessJavaVersionProbe>();
        services.AddSingleton<IJavaRuntimeValidator, JavaRuntimeValidator>();
        services.AddSingleton<IJavaRuntimeUninstallService, JavaRuntimeUninstallService>();
        return services;
    }
}
