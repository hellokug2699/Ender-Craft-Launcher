// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using Ecl.Configuration.Network;
using Ecl.Configuration.Settings;
using Ecl.Core;
using Ecl.Download;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ecl.Tests.Network;

/// <summary>
/// D-04 代理执行链：用本地 HTTP / SOCKS5 代理服务验证
/// <see cref="SettingsHttpClientProvider"/> 与 <see cref="ProxyConnectionTester"/> 的完整请求链路。
/// 不访问任何外网，也不使用 ECL 的真实下载/安装流程。
/// </summary>
public sealed class ProxyExecutionTests
{
    [Fact]
    public async Task HttpProxyWithBasicAuthenticationIsUsedForTheWholeRequest()
    {
        using var proxy = new LocalProxyServer(LocalProxyKind.Http)
        {
            RequireCredentials = true,
            ExpectedUserName = "alice",
            ExpectedPassword = "secret",
        };
        using var environment = new TestEnvironment();
        using var provider = environment.CreateServices();
        var settings = provider.GetRequiredService<ISettingsService>();
        ConfigureManual(settings, "http", "127.0.0.1", proxy.Port);
        var endpoint = ProxyEndpoint.TryCreate("http", "127.0.0.1", proxy.Port, out var created, out _)
            ? created!
            : throw new InvalidOperationException("测试端点无效。");
        var store = new FakeCredentialStore();
        store.Saved[endpoint.ToString()] = new ProxyCredential("alice", "secret");

        using var client = new SettingsHttpClientProvider(
                settings,
                store,
                NullLogger<SettingsHttpClientProvider>.Instance)
            .CreateClient();
        client.Timeout = TimeSpan.FromSeconds(10);

        using var response = await client.GetAsync("http://example.test/");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", body);
        Assert.True(proxy.SawProxyAuthorization);
        Assert.Equal(1, proxy.SuccessfulRequests);
    }

    [Fact]
    public async Task Socks5ProxyWithCredentialsIsUsedForTheWholeRequest()
    {
        using var proxy = new LocalProxyServer(LocalProxyKind.Socks5)
        {
            RequireCredentials = true,
            ExpectedUserName = "alice",
            ExpectedPassword = "secret",
        };
        using var environment = new TestEnvironment();
        using var provider = environment.CreateServices();
        var settings = provider.GetRequiredService<ISettingsService>();
        ConfigureManual(settings, "socks5", "127.0.0.1", proxy.Port);
        var endpoint = ProxyEndpoint.TryCreate("socks5", "127.0.0.1", proxy.Port, out var created, out _)
            ? created!
            : throw new InvalidOperationException("测试端点无效。");
        var store = new FakeCredentialStore();
        store.Saved[endpoint.ToString()] = new ProxyCredential("alice", "secret");

        using var client = new SettingsHttpClientProvider(
                settings,
                store,
                NullLogger<SettingsHttpClientProvider>.Instance)
            .CreateClient();
        client.Timeout = TimeSpan.FromSeconds(10);

        using var response = await client.GetAsync("http://example.test/");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", body);
        Assert.Equal(1, proxy.SuccessfulRequests);
    }

    [Fact]
    public async Task NoneModeConnectsDirectlyToALocalOrigin()
    {
        using var origin = new LocalOriginServer();
        using var environment = new TestEnvironment();
        using var provider = environment.CreateServices();
        var settings = provider.GetRequiredService<ISettingsService>();
        settings.TrySetRaw(ProxySettings.ModeKey, ProxySettings.NoneMode, null, out _);

        using var client = new SettingsHttpClientProvider(
                settings,
                new FakeCredentialStore(),
                NullLogger<SettingsHttpClientProvider>.Instance)
            .CreateClient();
        client.Timeout = TimeSpan.FromSeconds(10);

        using var response = await client.GetAsync($"http://127.0.0.1:{origin.Port}/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("origin", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task InvalidManualProxyFailsExplicitlyAndNeverFallsBackToSystem()
    {
        using var environment = new TestEnvironment();
        using var provider = environment.CreateServices();
        var settings = provider.GetRequiredService<ISettingsService>();
        settings.TrySetRaw(ProxySettings.ModeKey, ProxySettings.ManualMode, null, out _);
        settings.TrySetRaw(ProxySettings.ProtocolKey, "http", null, out _);
        settings.TrySetRaw(ProxySettings.HostKey, string.Empty, null, out _);
        settings.TrySetRaw(ProxySettings.PortKey, string.Empty, null, out _);

        using var client = new SettingsHttpClientProvider(
                settings,
                new FakeCredentialStore(),
                NullLogger<SettingsHttpClientProvider>.Instance)
            .CreateClient();

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetAsync("http://example.test/"));
        Assert.Contains("手动代理配置无效", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TesterReportsReachedTargetAndProxyAuthenticationRequirement()
    {
        using var proxy = new LocalProxyServer(LocalProxyKind.Http)
        {
            RequireCredentials = true,
            ExpectedUserName = "alice",
            ExpectedPassword = "secret",
        };
        var endpoint = ProxyEndpoint.TryCreate("http", "127.0.0.1", proxy.Port, out var created, out _)
            ? created!
            : throw new InvalidOperationException("测试端点无效。");
        var tester = new ProxyConnectionTester();

        var withoutCredential = await tester.TestAsync(new ProxyTestRequest
        {
            Mode = ProxySettings.ManualMode,
            Endpoint = endpoint,
            Target = new Uri("http://example.test/"),
        });
        Assert.True(withoutCredential.Reached);
        Assert.Equal(407, withoutCredential.StatusCode);

        var withCredential = await tester.TestAsync(new ProxyTestRequest
        {
            Mode = ProxySettings.ManualMode,
            Endpoint = endpoint,
            Credential = new ProxyCredential("alice", "secret"),
            Target = new Uri("http://example.test/"),
        });
        Assert.True(withCredential.Reached);
        Assert.Equal(200, withCredential.StatusCode);
    }

    [Fact]
    public async Task TesterReportsARealConnectionFailure()
    {
        var port = GetFreePort();
        var endpoint = ProxyEndpoint.TryCreate("http", "127.0.0.1", port, out var created, out _)
            ? created!
            : throw new InvalidOperationException("测试端点无效。");
        var result = await new ProxyConnectionTester().TestAsync(new ProxyTestRequest
        {
            Mode = ProxySettings.ManualMode,
            Endpoint = endpoint,
            Target = new Uri("http://example.test/"),
            Timeout = TimeSpan.FromSeconds(3),
        });

        Assert.False(result.Reached);
        Assert.Equal("network.test.failed", result.FailureCode);
        Assert.False(string.IsNullOrWhiteSpace(result.Detail));
    }

    [Fact]
    public async Task TesterCanTestTheNoneModeAgainstALocalOrigin()
    {
        using var origin = new LocalOriginServer();
        var result = await new ProxyConnectionTester().TestAsync(new ProxyTestRequest
        {
            Mode = ProxySettings.NoneMode,
            Target = new Uri($"http://127.0.0.1:{origin.Port}/"),
        });

        Assert.True(result.Reached);
        Assert.Equal(200, result.StatusCode);
    }

    private static void ConfigureManual(ISettingsService settings, string protocol, string host, int port)
    {
        Assert.True(settings.TrySetRaw(ProxySettings.ModeKey, ProxySettings.ManualMode, null, out var error), error);
        Assert.True(settings.TrySetRaw(ProxySettings.ProtocolKey, protocol, null, out error), error);
        Assert.True(settings.TrySetRaw(ProxySettings.HostKey, host, null, out error), error);
        Assert.True(settings.TrySetRaw(ProxySettings.PortKey, port.ToString(), null, out error), error);
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class FakeCredentialStore : IProxyCredentialStore
    {
        public bool IsSupported => true;

        public Dictionary<string, ProxyCredential> Saved { get; } = new(StringComparer.Ordinal);

        public Result<ProxyCredential?> Read(ProxyEndpoint endpoint) =>
            Saved.TryGetValue(endpoint.ToString(), out var credential)
                ? Result<ProxyCredential?>.Ok(credential)
                : Result<ProxyCredential?>.Ok(null);

        public Result Save(ProxyEndpoint endpoint, ProxyCredential credential)
        {
            Saved[endpoint.ToString()] = credential;
            return Result.Ok();
        }

        public Result Delete(ProxyEndpoint endpoint)
        {
            Saved.Remove(endpoint.ToString());
            return Result.Ok();
        }
    }

    private enum LocalProxyKind
    {
        Http,
        Socks5,
    }

    /// <summary>
    /// 本地一次性/长驻代理服务：HTTP 代理支持 Basic 认证与 407 重试；
    /// SOCKS5 支持无认证与用户名/密码认证。只用于自动化测试，不进入产品代码。
    /// </summary>
    private sealed class LocalProxyServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptTask;
        private int _successfulRequests;
        private bool _sawProxyAuthorization;

        public LocalProxyServer(LocalProxyKind kind)
        {
            Kind = kind;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _acceptTask = Task.Run(AcceptLoopAsync);
        }

        public LocalProxyKind Kind { get; }

        public int Port { get; }

        public bool RequireCredentials { get; init; }

        public string ExpectedUserName { get; init; } = string.Empty;

        public string ExpectedPassword { get; init; } = string.Empty;

        public int SuccessfulRequests => Volatile.Read(ref _successfulRequests);

        public bool SawProxyAuthorization => Volatile.Read(ref _sawProxyAuthorization);

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            try
            {
                _acceptTask.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
                // 测试清理失败不应影响断言。
            }

            _cts.Dispose();
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (SocketException)
                {
                    return;
                }

                _ = Task.Run(() => HandleAsync(client), _cts.Token);
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            using (var stream = client.GetStream())
            {
                try
                {
                    if (Kind == LocalProxyKind.Http)
                    {
                        await HandleHttpAsync(stream);
                    }
                    else
                    {
                        await HandleSocks5Async(stream);
                    }
                }
                catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException)
                {
                    // 客户端提前关闭是测试中的正常路径。
                }
            }
        }

        private async Task HandleHttpAsync(NetworkStream stream)
        {
            var headers = await ReadHeadersAsync(stream);
            if (headers.Length == 0)
            {
                return;
            }

            var authorization = GetHeader(headers, "Proxy-Authorization");
            if (RequireCredentials)
            {
                if (string.IsNullOrWhiteSpace(authorization))
                {
                    await WriteAsync(
                        stream,
                        "HTTP/1.1 407 Proxy Authentication Required\r\n"
                        + "Proxy-Authenticate: Basic realm=\"test\"\r\n"
                        + "Content-Length: 0\r\nConnection: close\r\n\r\n");
                    return;
                }

                Volatile.Write(ref _sawProxyAuthorization, true);
                if (!IsExpectedBasicAuthorization(authorization))
                {
                    await WriteAsync(
                        stream,
                        "HTTP/1.1 407 Proxy Authentication Required\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    return;
                }
            }

            await WriteAsync(
                stream,
                "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
            Interlocked.Increment(ref _successfulRequests);
        }

        private async Task HandleSocks5Async(NetworkStream stream)
        {
            var greeting = new byte[2];
            await stream.ReadExactlyAsync(greeting, _cts.Token);
            var methods = new byte[greeting[1]];
            await stream.ReadExactlyAsync(methods, _cts.Token);

            if (RequireCredentials)
            {
                if (!methods.Contains((byte)0x02))
                {
                    await stream.WriteAsync(new byte[] { 0x05, 0xFF }, _cts.Token);
                    return;
                }

                await stream.WriteAsync(new byte[] { 0x05, 0x02 }, _cts.Token);
                var version = new byte[2];
                await stream.ReadExactlyAsync(version, _cts.Token);
                var user = new byte[version[1]];
                await stream.ReadExactlyAsync(user, _cts.Token);
                var passwordLength = new byte[1];
                await stream.ReadExactlyAsync(passwordLength, _cts.Token);
                var password = new byte[passwordLength[0]];
                await stream.ReadExactlyAsync(password, _cts.Token);
                if (Encoding.UTF8.GetString(user) != ExpectedUserName
                    || Encoding.UTF8.GetString(password) != ExpectedPassword)
                {
                    await stream.WriteAsync(new byte[] { 0x01, 0x01 }, _cts.Token);
                    return;
                }

                await stream.WriteAsync(new byte[] { 0x01, 0x00 }, _cts.Token);
            }
            else
            {
                await stream.WriteAsync(new byte[] { 0x05, 0x00 }, _cts.Token);
            }

            var request = new byte[4];
            await stream.ReadExactlyAsync(request, _cts.Token);
            var addressType = request[3];
            if (addressType == 0x01)
            {
                await stream.ReadExactlyAsync(new byte[6], _cts.Token);
            }
            else if (addressType == 0x03)
            {
                var length = new byte[1];
                await stream.ReadExactlyAsync(length, _cts.Token);
                await stream.ReadExactlyAsync(new byte[length[0] + 2], _cts.Token);
            }
            else
            {
                await stream.ReadExactlyAsync(new byte[18], _cts.Token);
            }

            await stream.WriteAsync(
                new byte[] { 0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 },
                _cts.Token);

            var headers = await ReadHeadersAsync(stream);
            if (headers.Length > 0)
            {
                await WriteAsync(
                    stream,
                    "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
                Interlocked.Increment(ref _successfulRequests);
            }
        }

        private bool IsExpectedBasicAuthorization(string header)
        {
            var value = header["Basic ".Length..].Trim();
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(value));
            return decoded == $"{ExpectedUserName}:{ExpectedPassword}";
        }

        private static string GetHeader(string headers, string name)
        {
            foreach (var line in headers.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = line.IndexOf(':', StringComparison.Ordinal);
                if (separator > 0
                    && string.Equals(line[..separator].Trim(), name, StringComparison.OrdinalIgnoreCase))
                {
                    return line[(separator + 1)..].Trim();
                }
            }

            return string.Empty;
        }

        private static async Task<string> ReadHeadersAsync(NetworkStream stream)
        {
            var buffer = new byte[4096];
            var builder = new StringBuilder();
            while (!builder.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var read = await stream.ReadAsync(buffer);
                if (read == 0)
                {
                    return builder.ToString();
                }

                builder.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }

            return builder.ToString();
        }

        private static async Task WriteAsync(NetworkStream stream, string text)
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(text));
            await stream.FlushAsync();
        }
    }

    private sealed class LocalOriginServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptTask;

        public LocalOriginServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _acceptTask = Task.Run(AcceptLoopAsync);
        }

        public int Port { get; }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            try
            {
                _acceptTask.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
                // 测试清理失败不应影响断言。
            }

            _cts.Dispose();
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (SocketException)
                {
                    return;
                }

                _ = Task.Run(async () =>
                {
                    using (client)
                    using (var stream = client.GetStream())
                    {
                        var buffer = new byte[1];
                        await stream.ReadExactlyAsync(buffer, _cts.Token);
                        await stream.WriteAsync(
                            Encoding.ASCII.GetBytes(
                                "HTTP/1.1 200 OK\r\nContent-Length: 6\r\nConnection: close\r\n\r\norigin"),
                            _cts.Token);
                    }
                });
            }
        }
    }
}
