// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ecl.Operations;

public static class OperationServiceCollectionExtensions
{
    /// <summary>
    /// 注册进程内任务中心。读取端（<see cref="IOperationStore"/>）与写入端
    /// （<see cref="IOperationService"/>）解析到同一个实例。
    /// </summary>
    public static IServiceCollection AddEclOperations(
        this IServiceCollection services,
        OperationServiceOptions? options = null)
    {
        if (options is not null)
        {
            services.TryAddSingleton(options);
        }

        services.TryAddSingleton<OperationService>();
        services.TryAddSingleton<IOperationService>(provider => provider.GetRequiredService<OperationService>());
        services.TryAddSingleton<IOperationStore>(provider => provider.GetRequiredService<OperationService>());
        return services;
    }
}
