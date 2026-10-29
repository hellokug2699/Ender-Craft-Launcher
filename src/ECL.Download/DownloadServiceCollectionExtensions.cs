// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.Extensions.DependencyInjection;

namespace Ecl.Download;

public static class DownloadServiceCollectionExtensions
{
    public static IServiceCollection AddEclDownload(this IServiceCollection services)
    {
        services.AddSingleton<IHttpClientProvider, SettingsHttpClientProvider>();
        services.AddSingleton<DownloadSourceResolver>();
        services.AddSingleton<DownloadService>();
        services.AddSingleton<IDownloadService>(provider => provider.GetRequiredService<DownloadService>());
        services.AddSingleton<IDownloadController>(provider => provider.GetRequiredService<DownloadService>());
        return services;
    }
}
