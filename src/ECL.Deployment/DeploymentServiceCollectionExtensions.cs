// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Deployment.Startup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ecl.Deployment;

/// <summary>
/// F-12 安装与桌面生命周期的服务注册。组合根（App）在建立容器后调用一次；
/// 这里只注册领域入口，不启动任何后台工作，也不碰注册表——注册表只在
/// <see cref="AutoStartReconciler.Reconcile"/> 或用户显式切换开关时才会被写入。
/// </summary>
public static class DeploymentServiceCollectionExtensions
{
    public static IServiceCollection AddEclDeploymentStartup(this IServiceCollection services)
    {
        services.TryAddSingleton<IRunKeyStore, HkcuRunKeyStore>();
        services.TryAddSingleton<IExecutableLocator>(_ => new InstallExecutableLocator());
        services.TryAddSingleton<IAutoStartRegistration>(provider => new AutoStartRegistration(
            provider.GetRequiredService<IRunKeyStore>(),
            provider.GetRequiredService<IExecutableLocator>(),
            provider.GetService<ILogger<AutoStartRegistration>>() ?? NullLogger<AutoStartRegistration>.Instance));
        services.TryAddSingleton(provider => new AutoStartReconciler(
            provider.GetRequiredService<IAutoStartRegistration>(),
            provider.GetRequiredService<Ecl.Configuration.Settings.ISettingsService>(),
            provider.GetService<ILogger<AutoStartReconciler>>() ?? NullLogger<AutoStartReconciler>.Instance));
        return services;
    }
}
