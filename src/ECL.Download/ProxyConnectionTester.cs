// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Ecl.Configuration.Network;

namespace Ecl.Download;

/// <summary>
/// 一次代理连通性测试请求。凭据只存在于此次调用内，不写入配置、日志或可序列化 UI 状态；
/// <see cref="ToString"/> 不包含凭据。
/// </summary>
public sealed class ProxyTestRequest
{
    public string Mode { get; init; } = ProxySettings.SystemMode;

    public ProxyEndpoint? Endpoint { get; init; }

    public ProxyCredential? Credential { get; init; }

    public required Uri Target { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

    public override string ToString() =>
        Endpoint is null ? $"ProxyTest({Mode})" : $"ProxyTest({Mode}, {Endpoint})";
}

/// <summary>
/// 测试结果只说明指定目标是否可达，不保证所有下载源/API 都能访问（settings-network.md §4）。
/// </summary>
public sealed record ProxyConnectionTestResult(
    bool Reached,
    int? StatusCode,
    TimeSpan Elapsed,
    string? FailureCode = null,
    string? Detail = null);

public interface IProxyConnectionTester
{
    Task<ProxyConnectionTestResult> TestAsync(
        ProxyTestRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 用与 <see cref="SettingsHttpClientProvider"/> 相同的手动端点/凭据构造方式测试指定目标。
/// 失败时返回真实阶段/原因，不回退系统代理，也不把 4xx/407 伪装成连接成功。
/// </summary>
public sealed class ProxyConnectionTester : IProxyConnectionTester
{
    public async Task<ProxyConnectionTestResult> TestAsync(
        ProxyTestRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Target);

        var mode = ProxyProtocols.Normalize(request.Mode);
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = request.Timeout,
        };

        switch (mode)
        {
            case ProxySettings.NoneMode:
                handler.UseProxy = false;
                break;
            case ProxySettings.SystemMode:
                handler.UseProxy = true;
                break;
            case ProxySettings.ManualMode:
                if (request.Endpoint is null)
                {
                    return new ProxyConnectionTestResult(
                        Reached: false,
                        StatusCode: null,
                        Elapsed: TimeSpan.Zero,
                        FailureCode: "network.test.endpoint_invalid",
                        Detail: "手动代理端点无效。");
                }

                var proxy = new WebProxy(request.Endpoint.ToUri());
                if (request.Credential is not null)
                {
                    proxy.Credentials = new NetworkCredential(
                        request.Credential.UserName,
                        request.Credential.Password);
                }

                handler.Proxy = proxy;
                handler.UseProxy = true;
                break;
            default:
                return new ProxyConnectionTestResult(
                    Reached: false,
                    StatusCode: null,
                    Elapsed: TimeSpan.Zero,
                    FailureCode: "network.test.invalid_mode",
                    Detail: "代理模式无效。");
        }

        using var client = new HttpClient(handler) { Timeout = request.Timeout };
        using var message = new HttpRequestMessage(HttpMethod.Get, request.Target);
        message.Headers.UserAgent.ParseAdd("ECL-NetworkDiagnostics/1.0");
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));

        var watch = Stopwatch.StartNew();
        try
        {
            using var response = await client.SendAsync(
                message,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            watch.Stop();
            return new ProxyConnectionTestResult(
                Reached: true,
                StatusCode: (int)response.StatusCode,
                Elapsed: watch.Elapsed);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            watch.Stop();
            return new ProxyConnectionTestResult(
                Reached: false,
                StatusCode: null,
                Elapsed: watch.Elapsed,
                FailureCode: "network.test.timeout",
                Detail: "连接测试超时。");
        }
        catch (HttpRequestException exception)
        {
            watch.Stop();
            return new ProxyConnectionTestResult(
                Reached: false,
                StatusCode: null,
                Elapsed: watch.Elapsed,
                FailureCode: "network.test.failed",
                Detail: exception.InnerException is null
                    ? exception.Message
                    : $"{exception.Message} {exception.InnerException.Message}");
        }
        catch (IOException exception)
        {
            watch.Stop();
            return new ProxyConnectionTestResult(
                Reached: false,
                StatusCode: null,
                Elapsed: watch.Elapsed,
                FailureCode: "network.test.failed",
                Detail: exception.Message);
        }
    }
}
