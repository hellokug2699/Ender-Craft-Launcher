// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Operations;
using Microsoft.Extensions.DependencyInjection;

namespace Ecl.Download;

public static class DownloadServiceCollectionExtensions
{
    public static IServiceCollection AddEclDownload(this IServiceCollection services)
    {
        // 下载是第一个接入统一任务中心的服务；Operation Store 同时供 UI / CLI 读取。
        services.AddEclOperations();
        services.AddSingleton<IHttpClientProvider, SettingsHttpClientProvider>();
        services.AddSingleton<DownloadSourceResolver>();
        services.AddSingleton<DownloadService>();
        services.AddSingleton<IDownloadService>(provider => provider.GetRequiredService<DownloadService>());
        services.AddSingleton<IDownloadController>(provider => provider.GetRequiredService<DownloadService>());
        return services;
    }
}
