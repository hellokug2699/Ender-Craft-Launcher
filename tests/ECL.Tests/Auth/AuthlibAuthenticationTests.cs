// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Ecl.Auth;
using Xunit;

namespace Ecl.Tests.Auth;

public sealed class AuthlibAuthenticationTests
{
    private static readonly AuthServer Server = new()
    {
        Id = "example.com-1a2b",
        Url = "https://example.com/api/yggdrasil",
        Name = "Example Auth",
    };

    [Fact]
    public async Task ParsesServerMetadata()
    {
        const string payload = """
            {
              "meta": {
                "serverName": "Example Auth",
                "implementationName": "authlib-injector",
                "implementationVersion": "1.2.8"
              },
              "skinDomains": ["example.com"],
              "signaturePublickey": "-----BEGIN PUBLIC KEY-----"
            }
            """;
        var metadata = AuthlibAuthenticationService.ParseMetadata(Server.Url, payload);

        Assert.True(metadata.IsSuccess, metadata.Error?.ToString());
        Assert.Equal("Example Auth", metadata.Value!.ServerName);
        Assert.Equal("authlib-injector", metadata.Value.ImplementationName);
        Assert.Equal(["example.com"], metadata.Value.SkinDomains);
    }

    [Fact]
    public void RejectsNonMetadataPayloads()
    {
        var html = AuthlibAuthenticationService.ParseMetadata(Server.Url, "<html>not an auth server</html>");
        Assert.False(html.IsSuccess);
        Assert.Equal("authserver.invalid_metadata", html.Error?.Code);

        var wrongJson = AuthlibAuthenticationService.ParseMetadata(Server.Url, "{\"foo\":1}");
        Assert.False(wrongJson.IsSuccess);
        Assert.Equal("authserver.invalid_metadata", wrongJson.Error?.Code);
    }

    [Fact]
    public async Task AuthenticateSendsYggdrasilPayloadAndParsesProfile()
    {
        var handler = new StubAuthlibHandler
        {
            AuthenticateResponse = """
                {
                  "accessToken": "ygg-access",
                  "clientToken": "server-client-token",
                  "availableProfiles": [{"id": "0123456789abcdef0123456789abcdef", "name": "Steve"}],
                  "selectedProfile": {"id": "0123456789abcdef0123456789abcdef", "name": "Steve"}
                }
                """,
        };
        using var httpClient = new HttpClient(handler);
        var service = new AuthlibAuthenticationService(httpClient);

        var result = await service.AuthenticateAsync(Server, "Steve", "secret-password");

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Equal("ygg-access", result.Value!.AccessToken);
        Assert.Equal("server-client-token", result.Value.ClientToken);
        Assert.Equal("Steve", result.Value.PlayerName);
        Assert.Equal("01234567-89ab-cdef-0123-456789abcdef", result.Value.Uuid);
        Assert.Equal(1, result.Value.AvailableProfiles);

        using var body = JsonDocument.Parse(handler.LastAuthenticateBody);
        var root = body.RootElement;
        Assert.Equal("Steve", root.GetProperty("username").GetString());
        Assert.Equal("secret-password", root.GetProperty("password").GetString());
        Assert.Equal("Minecraft", root.GetProperty("agent").GetProperty("name").GetString());
        Assert.Equal(1, root.GetProperty("agent").GetProperty("version").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("clientToken").GetString()));
        Assert.EndsWith("/authserver/authenticate", handler.LastRequestUri!.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthenticationFailureDoesNotEchoPasswordOrToken()
    {
        var handler = new StubAuthlibHandler
        {
            AuthenticateStatus = HttpStatusCode.Forbidden,
            AuthenticateResponse = """
                {"error":"ForbiddenOperationException","errorMessage":"Invalid credentials. Invalid username or password."}
                """,
        };
        using var httpClient = new HttpClient(handler);
        var service = new AuthlibAuthenticationService(httpClient);

        var result = await service.AuthenticateAsync(Server, "Steve", "secret-password");

        Assert.False(result.IsSuccess);
        Assert.Equal("auth.thirdparty_rejected", result.Error?.Code);
        Assert.Contains("Invalid credentials", result.Error!.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-password", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AccountWithoutSelectedProfileIsRejected()
    {
        var handler = new StubAuthlibHandler
        {
            AuthenticateResponse = """
                {"accessToken":"ygg-access","clientToken":"c","availableProfiles":[]}
                """,
        };
        using var httpClient = new HttpClient(handler);
        var service = new AuthlibAuthenticationService(httpClient);

        var result = await service.AuthenticateAsync(Server, "Steve", "pw");

        Assert.False(result.IsSuccess);
        Assert.Equal("auth.thirdparty_no_profile", result.Error?.Code);
    }

    [Fact]
    public async Task EmptyCredentialsFailWithoutNetworkCall()
    {
        var handler = new StubAuthlibHandler();
        using var httpClient = new HttpClient(handler);
        var service = new AuthlibAuthenticationService(httpClient);

        var result = await service.AuthenticateAsync(Server, " ", "");

        Assert.False(result.IsSuccess);
        Assert.Equal("auth.thirdparty_credentials_required", result.Error?.Code);
        Assert.Null(handler.LastRequestUri);
    }

    [Fact]
    public async Task ValidateReportsInvalidTokenAsFalse()
    {
        var handler = new StubAuthlibHandler { ValidateStatus = HttpStatusCode.Forbidden };
        using var httpClient = new HttpClient(handler);
        var service = new AuthlibAuthenticationService(httpClient);

        var result = await service.ValidateAsync(Server, "ygg-access", "client-token");

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.False(result.Value);
        Assert.EndsWith("/authserver/validate", handler.LastRequestUri!.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateReportsValidTokenAsTrue()
    {
        var handler = new StubAuthlibHandler { ValidateStatus = HttpStatusCode.NoContent };
        using var httpClient = new HttpClient(handler);
        var service = new AuthlibAuthenticationService(httpClient);

        var result = await service.ValidateAsync(Server, "ygg-access", "client-token");

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.True(result.Value);
    }

    [Fact]
    public async Task RefreshReturnsRotatedTokens()
    {
        var handler = new StubAuthlibHandler
        {
            RefreshResponse = """
                {
                  "accessToken": "new-access",
                  "clientToken": "new-client-token",
                  "selectedProfile": {"id": "0123456789abcdef0123456789abcdef", "name": "Steve"}
                }
                """,
        };
        using var httpClient = new HttpClient(handler);
        var service = new AuthlibAuthenticationService(httpClient);

        var result = await service.RefreshAsync(Server, "old-access", "client-token");

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Equal("new-access", result.Value!.AccessToken);
        Assert.Equal("new-client-token", result.Value.ClientToken);
        Assert.Equal("Steve", result.Value.PlayerName);
        Assert.EndsWith("/authserver/refresh", handler.LastRequestUri!.AbsolutePath, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef", "01234567-89ab-cdef-0123-456789abcdef")]
    [InlineData("01234567-89ab-cdef-0123-456789abcdef", "01234567-89ab-cdef-0123-456789abcdef")]
    public void NormalizesProfileUuid(string raw, string expected) =>
        Assert.Equal(expected, AuthlibAuthenticationService.NormalizeUuid(raw));

    private sealed class StubAuthlibHandler : HttpMessageHandler
    {
        public string AuthenticateResponse { get; init; } = """{"accessToken":"a","clientToken":"c"}""";

        public HttpStatusCode AuthenticateStatus { get; init; } = HttpStatusCode.OK;

        public string RefreshResponse { get; init; } = """{"accessToken":"a","clientToken":"c"}""";

        public HttpStatusCode ValidateStatus { get; init; } = HttpStatusCode.NoContent;

        public Uri? LastRequestUri { get; private set; }

        public string LastAuthenticateBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            var path = request.RequestUri!.AbsolutePath;

            if (path.EndsWith("/authserver/authenticate", StringComparison.Ordinal))
            {
                LastAuthenticateBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                return Json(AuthenticateResponse, AuthenticateStatus);
            }

            if (path.EndsWith("/authserver/validate", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(ValidateStatus);
            }

            if (path.EndsWith("/authserver/refresh", StringComparison.Ordinal))
            {
                return Json(RefreshResponse, HttpStatusCode.OK);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("unexpected endpoint", Encoding.UTF8, "text/plain"),
            };
        }

        private static HttpResponseMessage Json(string content, HttpStatusCode status) => new(status)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json"),
        };
    }
}
