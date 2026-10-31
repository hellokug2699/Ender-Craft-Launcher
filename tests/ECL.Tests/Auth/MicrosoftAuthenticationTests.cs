// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using System.Net.Http;
using System.Text;
using Ecl.Auth;
using Xunit;

namespace Ecl.Tests.Auth;

public sealed class MicrosoftAuthenticationTests
{
    [Fact]
    public async Task SignInCompletesOAuthXboxXstsMinecraftChain()
    {
        var handler = new StubAuthenticationHandler();
        using var httpClient = new HttpClient(handler);
        var service = new MicrosoftAuthenticationService(httpClient);

        var options = new MicrosoftAuthenticationOptions
        {
            ClientId = "a1e97b44-9c00-4a60-8e32-1119fefe7aab",
        };

        var result = await service.SignInAsync(options, OpenBrowserAsync);

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Equal(AccountType.Microsoft, result.Value!.Type);
        Assert.Equal("TestPlayer", result.Value.Name);
        Assert.Equal("1234567890abcdef", result.Value.Uuid);
        Assert.Equal("minecraft-access", result.Value.AccessToken);
        Assert.Equal("microsoft-refresh", result.Value.RefreshToken);
        Assert.True(handler.XboxContractVersionSeen);
        Assert.True(handler.XstsContractVersionSeen);
        Assert.True(handler.MinecraftIdentityTokenSeen);

        async Task OpenBrowserAsync(Uri authorizeUri)
        {
            var state = GetQueryValue(authorizeUri, "state");
            Assert.False(string.IsNullOrWhiteSpace(state));

            using var browser = new HttpClient();
            var callback = new UriBuilder(authorizeUri.Scheme, authorizeUri.Host, authorizeUri.Port)
            {
                Query = $"code=authorization-code&state={Uri.EscapeDataString(state!)}",
            };
            using var response = await browser.GetAsync(callback.Uri);
            response.EnsureSuccessStatusCode();
        }
    }

    private static string? GetQueryValue(Uri uri, string key)
    {
        return uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .Where(parts => string.Equals(Uri.UnescapeDataString(parts[0]), key, StringComparison.Ordinal))
            .Select(parts => Uri.UnescapeDataString(parts[1]))
            .FirstOrDefault();
    }

    private sealed class StubAuthenticationHandler : HttpMessageHandler
    {
        public bool XboxContractVersionSeen { get; private set; }
        public bool XstsContractVersionSeen { get; private set; }
        public bool MinecraftIdentityTokenSeen { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/oauth2/v2.0/token", StringComparison.Ordinal))
            {
                return Json("{\"access_token\":\"microsoft-access\",\"refresh_token\":\"microsoft-refresh\",\"expires_in\":3600}");
            }

            if (request.RequestUri.AbsoluteUri == "https://user.auth.xboxlive.com/user/authenticate")
            {
                XboxContractVersionSeen = request.Headers.TryGetValues("x-xbl-contract-version", out var values) && values.Single() == "1";
                return Json("{\"Token\":\"xbox-user-token\",\"DisplayClaims\":{\"xui\":[{\"uhs\":\"test-uhs\"}]}}");
            }

            if (request.RequestUri.AbsoluteUri == "https://xsts.auth.xboxlive.com/xsts/authorize")
            {
                XstsContractVersionSeen = request.Headers.TryGetValues("x-xbl-contract-version", out var values) && values.Single() == "1";
                return Json("{\"Token\":\"xsts-token\",\"DisplayClaims\":{\"xui\":[{\"uhs\":\"test-uhs\"}]}}");
            }

            if (request.RequestUri.AbsoluteUri == "https://api.minecraftservices.com/authentication/login_with_xbox")
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                MinecraftIdentityTokenSeen = body.Contains("XBL3.0 x=test-uhs;xsts-token", StringComparison.Ordinal);
                return Json("{\"access_token\":\"minecraft-access\",\"expires_in\":3600}");
            }

            if (request.RequestUri.AbsoluteUri == "https://api.minecraftservices.com/entitlements/mcstore")
            {
                Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
                Assert.Equal("minecraft-access", request.Headers.Authorization.Parameter);
                return Json("{\"items\":[{\"name\":\"product_minecraft\"}]}");
            }

            if (request.RequestUri.AbsoluteUri == "https://api.minecraftservices.com/minecraft/profile")
            {
                Assert.Equal("minecraft-access", request.Headers.Authorization!.Parameter);
                return Json("{\"id\":\"1234567890abcdef\",\"name\":\"TestPlayer\"}");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("not found", Encoding.UTF8, "text/plain"),
            };
        }

        private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json"),
        };
    }
}
