// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.Extensions.DependencyInjection;

namespace Ecl.Download;

public static class DownloadServiceCollectionExtensions
{
    public static IServiceCollection AddEclDownload(this IServiceCollection services)
    {
        services.AddSingleton<IHttpClientProvider, SettingsHttpClientProvider>();
        services.AddSingleton<DownloadSourceResolver>();
        services.AddSingleton<IDownloadService, DownloadService>();
        return services;
    }
}
