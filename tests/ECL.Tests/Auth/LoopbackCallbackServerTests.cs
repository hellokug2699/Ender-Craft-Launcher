// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using System.Net.Http;
using Ecl.Auth;
using Ecl.Core;
using Xunit;

namespace Ecl.Tests.Auth;

/// <summary>
/// 真实 <see cref="HttpListener"/> 的 loopback 行为。CI 可能没有 HTTP.sys 的 URL ACL，
/// 因此真实监听用例在 <c>Start</c> 失败时提前返回；协议细节主要由假监听器覆盖。
/// </summary>
public sealed class LoopbackCallbackServerTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void LoopbackHostValidationAcceptsOnlyLocalAddresses()
    {
        Assert.True(HttpListenerLoopbackServer.IsLoopbackHost("localhost"));
        Assert.True(HttpListenerLoopbackServer.IsLoopbackHost("LOCALHOST"));
        Assert.True(HttpListenerLoopbackServer.IsLoopbackHost("127.0.0.1"));
        Assert.True(HttpListenerLoopbackServer.IsLoopbackHost("[::1]"));
        Assert.False(HttpListenerLoopbackServer.IsLoopbackHost("0.0.0.0"));
        Assert.False(HttpListenerLoopbackServer.IsLoopbackHost("192.168.1.20"));
        Assert.False(HttpListenerLoopbackServer.IsLoopbackHost("localhost.evil.example"));
        Assert.False(HttpListenerLoopbackServer.IsLoopbackHost(""));
    }

    [Fact]
    public void StartRejectsNonLoopbackHost()
    {
        var result = HttpListenerLoopbackServer.Start("example.com", 0, null);

        Assert.False(result.IsSuccess);
        Assert.Equal("auth.invalid_redirect_host", result.Error!.Code);
    }

    [Fact]
    public void StartRejectsOutOfRangePort()
    {
        var result = HttpListenerLoopbackServer.Start("localhost", IPEndPoint.MaxPort + 1, null);

        Assert.False(result.IsSuccess);
        Assert.Equal("auth.callback_listener_unavailable", result.Error!.Code);
    }

    [Fact]
    public async Task RealListenerIgnoresProbesAndReturnsValidCallback()
    {
        var started = HttpListenerLoopbackServer.Start("localhost", 0, null);
        if (!started.IsSuccess)
        {
            // 无 URL ACL 的 CI 环境无法绑定 HTTP.sys；协议正确性已由假监听器测试覆盖。
            return;
        }

        await using var server = started.Value!;
        using var client = new HttpClient { BaseAddress = server.RedirectUri };
        var waiting = server.WaitForCallbackAsync(CancellationToken.None);

        var favicon = await client.GetAsync("favicon.ico");
        Assert.Equal(HttpStatusCode.NotFound, favicon.StatusCode);
        Assert.False(waiting.IsCompleted);

        var wrongMethod = await client.PostAsync(string.Empty, new StringContent(string.Empty));
        Assert.Equal(HttpStatusCode.NotFound, wrongMethod.StatusCode);
        Assert.False(waiting.IsCompleted);

        var missingCode = await client.GetAsync("?probe=1");
        Assert.Equal(HttpStatusCode.BadRequest, missingCode.StatusCode);
        Assert.False(waiting.IsCompleted);

        var valid = await client.GetAsync("?code=the-code&state=the-state");
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);

        var callback = await waiting.WaitAsync(TestTimeout);
        Assert.NotNull(callback);
        Assert.Equal("GET", callback!.Method);
        Assert.Equal("/", callback.Path);
        Assert.Equal("the-code", callback.Query["code"]);
        Assert.Equal("the-state", callback.Query["state"]);
    }

    [Fact]
    public async Task OccupiedPortFailsAndDisposeReleasesIt()
    {
        var first = HttpListenerLoopbackServer.Start("localhost", 0, null);
        if (!first.IsSuccess)
        {
            return;
        }

        var server = first.Value!;
        try
        {
            var port = server.RedirectUri.Port;
            var second = HttpListenerLoopbackServer.Start("localhost", port, null);
            Assert.False(second.IsSuccess);
            Assert.Equal("auth.callback_listener_unavailable", second.Error!.Code);

            await server.DisposeAsync();

            // HTTP.sys 释放注册可能有短暂延迟：失败时重试而不是让测试偶发红。
            Result<IMicrosoftLoopbackServer> third = default;
            for (var attempt = 0; attempt < 5; attempt++)
            {
                third = HttpListenerLoopbackServer.Start("localhost", port, null);
                if (third.IsSuccess)
                {
                    break;
                }

                await Task.Delay(100);
            }

            Assert.True(third.IsSuccess, third.Error?.ToString());
            await third.Value!.DisposeAsync();
        }
        finally
        {
            await server.DisposeAsync();
        }
    }
}
