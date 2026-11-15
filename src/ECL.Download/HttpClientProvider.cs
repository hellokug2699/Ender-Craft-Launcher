// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using System.Net.Http.Headers;
using Ecl.Configuration.Network;
using Ecl.Configuration.Settings;
using Microsoft.Extensions.Logging;

namespace Ecl.Download;

public interface IHttpClientProvider
{
    HttpClient CreateClient();
}

/// <summary>
/// 所有下载/内容 API 的唯一代理执行入口（D-04）。
///
/// - <c>none</c>：显式关闭代理；
/// - <c>system</c>：使用操作系统代理；
/// - <c>manual</c>：使用结构化端点，凭据从受保护存储读取；
/// - 手动配置无效时返回一个总是显式失败的客户端，绝不静默回退到系统代理。
/// </summary>
public sealed class SettingsHttpClientProvider(
    ISettingsService settings,
    IProxyCredentialStore credentials,
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

        var proxy = ProxySettings.Resolve(settings);
        switch (proxy.Mode)
        {
            case ProxyMode.None:
                handler.UseProxy = false;
                break;
            case ProxyMode.System:
                handler.UseProxy = true;
                break;
            case ProxyMode.Manual:
                if (proxy.Endpoint is null)
                {
                    logger.LogWarning(
                        "手动代理配置无效（{Code}），本次网络请求将显式失败而不回退系统代理。",
                        proxy.ErrorCode);
                    return new HttpClient(new InvalidProxyHandler(
                        "手动代理配置无效："
                        + (proxy.ErrorDetail ?? "请在设置 → 网络重新填写协议、主机与端口。")
                        + " 请在设置 → 网络修正后再试。"))
                    {
                        Timeout = Timeout.InfiniteTimeSpan,
                    };
                }

                var webProxy = new WebProxy(proxy.Endpoint.ToUri());
                if (credentials.IsSupported)
                {
                    var credential = credentials.Read(proxy.Endpoint);
                    if (credential.IsSuccess && credential.Value is not null)
                    {
                        webProxy.Credentials = new NetworkCredential(
                            credential.Value.UserName,
                            credential.Value.Password);
                    }
                    else if (!credential.IsSuccess)
                    {
                        // 读取失败不阻止请求：代理可能不需要认证；
                        // 如果确实需要，HTTP 407 会作为显式失败回传给调用方。
                        logger.LogWarning(
                            "读取代理凭据失败：{Code}",
                            credential.Error?.Code);
                    }
                }

                handler.Proxy = webProxy;
                handler.UseProxy = true;
                break;
        }

        var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"EnderCraftLauncher/{typeof(SettingsHttpClientProvider).Assembly.GetName().Version?.ToString(3) ?? "0.1"} (+https://github.com/hellokug2699/Ender-Craft-Launcher)");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        return client;
    }

    /// <summary>
    /// 手动代理配置无效时的显式失败入口：发送任何请求都返回清晰的错误，
    /// 而不是悄悄改用系统代理或直连。
    /// </summary>
    private sealed class InvalidProxyHandler(string message) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException(message));
    }
}
