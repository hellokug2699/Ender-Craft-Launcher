// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using System.Net.Http;
using System.Text;
using Ecl.Auth;
using Xunit;

namespace Ecl.Tests.Auth;

/// <summary>
/// 刷新路径必须完全静默：只使用 refresh token，不打开浏览器、不经过 loopback 回调，
/// 也不会重新拉取 entitlement / profile。
/// </summary>
public sealed class MicrosoftRefreshTests
{
    [Fact]
    public async Task RefreshCompletesChainWithoutBrowser()
    {
        var handler = new StubRefreshHandler(rotateRefreshToken: true);
        using var httpClient = new HttpClient(handler);
        var service = new MicrosoftAuthenticationService(httpClient);
        var options = new MicrosoftAuthenticationOptions { ClientId = "a1e97b44-9c00-4a60-8e32-1119fefe7aab" };

        var result = await service.RefreshAsync(options, "old-refresh");

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Equal("mc-access-new", result.Value!.MinecraftAccessToken);
        Assert.Equal("ms-refresh-new", result.Value.MicrosoftRefreshToken);
        Assert.NotNull(result.Value.MinecraftTokenExpiresAt);
        Assert.True(result.Value.MinecraftTokenExpiresAt > DateTimeOffset.UtcNow.AddHours(23));

        Assert.Contains("grant_type=refresh_token", handler.TokenBody);
        Assert.Contains("refresh_token=old-refresh", handler.TokenBody);
        Assert.Contains("client_id=a1e97b44-9c00-4a60-8e32-1119fefe7aab", handler.TokenBody);
        Assert.Contains("scope=XboxLive.signin+offline_access", handler.TokenBody);

        Assert.Contains("\"Properties\"", handler.XboxBody);
        Assert.Contains("\"AuthMethod\":\"RPS\"", handler.XboxBody);
        Assert.Contains("\"RpsTicket\":\"d=ms-access-new\"", handler.XboxBody);
        Assert.Equal("1", handler.XboxContractVersion);

        Assert.Contains("\"SandboxId\":\"RETAIL\"", handler.XstsBody);
        Assert.Contains("rp://api.minecraftservices.com/", handler.XstsBody);
        Assert.Equal("1", handler.XstsContractVersion);

        Assert.Contains("XBL3.0 x=uhs-1;xsts-token", handler.MinecraftBody);

        Assert.False(handler.EntitlementsOrProfileRequested);
    }

    [Fact]
    public async Task KeepsExistingRefreshTokenWhenServerDoesNotRotate()
    {
        var handler = new StubRefreshHandler(rotateRefreshToken: false);
        using var httpClient = new HttpClient(handler);
        var service = new MicrosoftAuthenticationService(httpClient);

        var result = await service.RefreshAsync(
            new MicrosoftAuthenticationOptions { ClientId = "a1e97b44-9c00-4a60-8e32-1119fefe7aab" },
            "old-refresh");

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Equal("old-refresh", result.Value!.MicrosoftRefreshToken);
    }

    [Fact]
    public async Task EmptyRefreshTokenFailsWithoutNetwork()
    {
        var handler = new StubRefreshHandler(rotateRefreshToken: true);
        using var httpClient = new HttpClient(handler);
        var service = new MicrosoftAuthenticationService(httpClient);

        var result = await service.RefreshAsync(
            new MicrosoftAuthenticationOptions { ClientId = "a1e97b44-9c00-4a60-8e32-1119fefe7aab" },
            "   ");

        Assert.False(result.IsSuccess);
        Assert.Equal("auth.credentials_missing", result.Error?.Code);
        Assert.Empty(handler.TokenBody);
    }

    private sealed class StubRefreshHandler(bool rotateRefreshToken) : HttpMessageHandler
    {
        public string TokenBody { get; private set; } = string.Empty;

        public string XboxBody { get; private set; } = string.Empty;

        public string XstsBody { get; private set; } = string.Empty;

        public string MinecraftBody { get; private set; } = string.Empty;

        public string? XboxContractVersion { get; private set; }

        public string? XstsContractVersion { get; private set; }

        public bool EntitlementsOrProfileRequested { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;

            if (uri.AbsolutePath.EndsWith("/oauth2/v2.0/token", StringComparison.Ordinal))
            {
                TokenBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                var rotated = rotateRefreshToken ? ",\"refresh_token\":\"ms-refresh-new\"" : string.Empty;
                return Json($"{{\"access_token\":\"ms-access-new\",\"expires_in\":3600{rotated}}}");
            }

            if (uri.AbsoluteUri == "https://user.auth.xboxlive.com/user/authenticate")
            {
                XboxBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                XboxContractVersion = ContractVersion(request);
                return Json("{\"Token\":\"xbox-user-token\",\"DisplayClaims\":{\"xui\":[{\"uhs\":\"uhs-1\"}]}}");
            }

            if (uri.AbsoluteUri == "https://xsts.auth.xboxlive.com/xsts/authorize")
            {
                XstsBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                XstsContractVersion = ContractVersion(request);
                return Json("{\"Token\":\"xsts-token\",\"DisplayClaims\":{\"xui\":[{\"uhs\":\"uhs-1\"}]}}");
            }

            if (uri.AbsoluteUri == "https://api.minecraftservices.com/authentication/login_with_xbox")
            {
                MinecraftBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                return Json("{\"access_token\":\"mc-access-new\",\"expires_in\":86400}");
            }

            EntitlementsOrProfileRequested = true;
            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("refresh must not call this endpoint", Encoding.UTF8, "text/plain"),
            };
        }

        private static string? ContractVersion(HttpRequestMessage request) =>
            request.Headers.TryGetValues("x-xbl-contract-version", out var values)
                ? values.Single()
                : null;

        private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json"),
        };
    }
}
