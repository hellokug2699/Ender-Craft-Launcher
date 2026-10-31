// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using System.Net.Http;
using System.Text.Json;
using Ecl.Auth;
using Xunit;

namespace Ecl.Tests.Auth;

public sealed class MicrosoftAuthenticationTests
{
    [Fact]
    public async Task RefreshAsync_MapsOAuthSnakeCase_AndXboxPascalCase_AndRotatesRefreshToken()
    {
        var handler = new FakeAuthenticationHandler();
        using var client = new HttpClient(handler);
        var service = new MicrosoftAuthenticationService(client);

        var result = await service.RefreshAsync(
            new MicrosoftAuthenticationOptions
            {
                ClientId = "a1e97b44-9c00-4a60-8e32-1119fefe7aab",
            },
            "old-refresh-token");

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal("minecraft-access-token", result.Value!.MinecraftAccessToken);
        Assert.Equal("rotated-refresh-token", result.Value.MicrosoftRefreshToken);
        Assert.Equal(3600, (result.Value.MinecraftTokenExpiresAt - DateTimeOffset.UtcNow)!.Value.TotalSeconds, precision: 0);

        Assert.Equal("old-refresh-token", handler.RefreshTokenReceived);
        Assert.Equal("minecraft-access-token", handler.AuthorizationTokenReceived);
        Assert.Equal("XboxLive.signin offline_access", handler.ScopeReceived);
        Assert.True(handler.XboxRequestWasPascalCase);
        Assert.True(handler.XstsRequestWasPascalCase);
    }

    [Fact]
    public async Task RefreshAsync_PreservesOldRefreshToken_WhenMicrosoftDoesNotRotateIt()
    {
        var handler = new FakeAuthenticationHandler { RotateRefreshToken = false };
        using var client = new HttpClient(handler);
        var service = new MicrosoftAuthenticationService(client);

        var result = await service.RefreshAsync(
            new MicrosoftAuthenticationOptions
            {
                ClientId = "a1e97b44-9c00-4a60-8e32-1119fefe7aab",
            },
            "existing-refresh-token");

        Assert.True(result.IsSuccess);
        Assert.Equal("existing-refresh-token", result.Value!.MicrosoftRefreshToken);
    }

    [Fact]
    public async Task RefreshAsync_RejectsInvalidClientIdBeforeNetworkRequest()
    {
        var handler = new FakeAuthenticationHandler();
        using var client = new HttpClient(handler);
        var service = new MicrosoftAuthenticationService(client);

        var result = await service.RefreshAsync(
            new MicrosoftAuthenticationOptions { ClientId = "not-a-guid" },
            "refresh-token");

        Assert.False(result.IsSuccess);
        Assert.Equal("auth.invalid_client_id", result.Error!.Code);
        Assert.Equal(0, handler.RequestCount);
    }

    private sealed class FakeAuthenticationHandler : HttpMessageHandler
    {
        public bool RotateRefreshToken { get; init; } = true;
        public int RequestCount { get; private set; }
        public string? RefreshTokenReceived { get; private set; }
        public string? ScopeReceived { get; private set; }
        public string? AuthorizationTokenReceived { get; private set; }
        public bool XboxRequestWasPascalCase { get; private set; }
        public bool XstsRequestWasPascalCase { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;

            if (request.RequestUri!.AbsolutePath.EndsWith("/oauth2/v2.0/token", StringComparison.Ordinal))
            {
                var form = await request.Content!.ReadAsStringAsync(cancellationToken);
                var values = form.Split('&', StringSplitOptions.RemoveEmptyEntries)
                    .Select(pair => pair.Split('=', 2))
                    .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]));
                RefreshTokenReceived = values["refresh_token"];
                ScopeReceived = values["scope"];

                return Json(HttpStatusCode.OK, new
                {
                    access_token = "microsoft-access-token",
                    refresh_token = RotateRefreshToken ? "rotated-refresh-token" : (string?)null,
                });
            }

            if (request.RequestUri == new Uri("https://user.auth.xboxlive.com/user/authenticate"))
            {
                var json = await request.Content!.ReadAsStringAsync(cancellationToken);
                using var document = JsonDocument.Parse(json);
                XboxRequestWasPascalCase = document.RootElement.TryGetProperty("Properties", out var properties)
                    && properties.TryGetProperty("AuthMethod", out _)
                    && properties.TryGetProperty("RpsTicket", out _)
                    && document.RootElement.TryGetProperty("RelyingParty", out _)
                    && document.RootElement.TryGetProperty("TokenType", out _);

                return Json(HttpStatusCode.OK, new
                {
                    Token = "xbox-token",
                    DisplayClaims = new { xui = new[] { new { uhs = "user-hash" } } },
                });
            }

            if (request.RequestUri == new Uri("https://xsts.auth.xboxlive.com/xsts/authorize"))
            {
                var json = await request.Content!.ReadAsStringAsync(cancellationToken);
                using var document = JsonDocument.Parse(json);
                XstsRequestWasPascalCase = document.RootElement.TryGetProperty("Properties", out var properties)
                    && properties.TryGetProperty("SandboxId", out _)
                    && properties.TryGetProperty("UserTokens", out _)
                    && document.RootElement.TryGetProperty("RelyingParty", out _)
                    && document.RootElement.TryGetProperty("TokenType", out _);

                return Json(HttpStatusCode.OK, new
                {
                    Token = "xsts-token",
                    DisplayClaims = new { xui = new[] { new { uhs = "user-hash" } } },
                });
            }

            if (request.RequestUri == new Uri("https://api.minecraftservices.com/authentication/login_with_xbox"))
            {
                var json = await request.Content!.ReadAsStringAsync(cancellationToken);
                using var document = JsonDocument.Parse(json);
                Assert.Equal("XBL3.0 x=user-hash;xsts-token", document.RootElement.GetProperty("identityToken").GetString());
                return Json(HttpStatusCode.OK, new { access_token = "minecraft-access-token", expires_in = 3600 });
            }

            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        }

        private static HttpResponseMessage Json(HttpStatusCode statusCode, object value) => new(statusCode)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), System.Text.Encoding.UTF8, "application/json"),
        };
    }
}
