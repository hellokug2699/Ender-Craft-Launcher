// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using System.Net.Http.Headers;
using Ecl.Configuration.Settings;
using Microsoft.Extensions.Logging;

namespace Ecl.Download;

public interface IHttpClientProvider
{
    HttpClient CreateClient();
}

public sealed class SettingsHttpClientProvider(
    ISettingsService settings,
    ILogger<SettingsHttpClientProvider> logger) : IHttpClientProvider
{
    public HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        };

        var proxyMode = settings.Get<string>("network.proxyMode");
        if (string.Equals(proxyMode, "none", StringComparison.OrdinalIgnoreCase))
        {
            handler.UseProxy = false;
        }
        else if (string.Equals(proxyMode, "manual", StringComparison.OrdinalIgnoreCase))
        {
            var address = settings.Get<string>("network.proxyAddress");
            if (Uri.TryCreate(address, UriKind.Absolute, out var proxyUri))
            {
                handler.Proxy = new WebProxy(proxyUri);
                handler.UseProxy = true;
            }
            else
            {
                logger.LogWarning("代理地址无效，将使用系统代理：{Address}", address);
            }
        }

        var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("EnderCraftLauncher/0.1");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        return client;
    }
}
