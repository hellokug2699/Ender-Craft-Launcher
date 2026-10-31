// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Auth;
using Ecl.Core;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecl.Tests.Auth;

public sealed class ThirdPartyAccountTests
{
    private const string ServerA = "https://auth-a.example.com/api/yggdrasil";
    private const string ServerB = "https://auth-b.example.com/api/yggdrasil";

    [Fact]
    public async Task AddServerValidatesMetadataAndIsIdempotent()
    {
        using var environment = new TestEnvironment();
        await using var provider = CreateProvider(environment, new StubAuthlibAuthentication());
        var servers = provider.GetRequiredService<IAuthServerService>();

        var added = await servers.AddAsync(ServerA);
        Assert.True(added.IsSuccess, added.Error?.ToString());
        Assert.Equal("Server auth-a.example.com", added.Value!.Name);
        Assert.Equal(ServerA, added.Value.Url);

        var again = await servers.AddAsync(ServerA + "/");
        Assert.True(again.IsSuccess);
        Assert.Equal(added.Value.Id, again.Value!.Id);
        Assert.Single(await servers.GetServersAsync());

        var invalid = await servers.AddAsync("not-a-url");
        Assert.False(invalid.IsSuccess);
        Assert.Equal("authserver.invalid_url", invalid.Error?.Code);
    }

    [Fact]
    public async Task SignInCreatesAccountBoundToServer()
    {
        using var environment = new TestEnvironment();
        var stub = new StubAuthlibAuthentication();
        await using var provider = CreateProvider(environment, stub);
        var servers = provider.GetRequiredService<IAuthServerService>();
        var accounts = provider.GetRequiredService<IAccountService>();

        var server = (await servers.AddAsync(ServerA)).Value!;
        var signedIn = await accounts.SignInThirdPartyAsync(server.Id, "Steve", "pw", makeDefault: true);

        Assert.True(signedIn.IsSuccess, signedIn.Error?.ToString());
        Assert.Equal(AccountType.ThirdParty, signedIn.Value!.Type);
        Assert.Equal("Steve", signedIn.Value.Name);
        Assert.Equal("01234567-89ab-cdef-0123-456789abcdef", signedIn.Value.Uuid);
        Assert.Equal(server.Id, signedIn.Value.AuthServerId);

        // 重启后仍然带着服务器绑定，并能从凭据存储恢复令牌。
        var defaultAccount = await accounts.GetDefaultAccountAsync();
        Assert.NotNull(defaultAccount);
        Assert.Equal(server.Id, defaultAccount!.AuthServerId);
        Assert.Equal("ygg-access-Steve", defaultAccount.AccessToken);
        Assert.Equal("client-Steve", defaultAccount.ClientToken);
    }

    [Fact]
    public async Task SameNameOnDifferentServersCreatesSeparateAccounts()
    {
        using var environment = new TestEnvironment();
        await using var provider = CreateProvider(environment, new StubAuthlibAuthentication());
        var servers = provider.GetRequiredService<IAuthServerService>();
        var accounts = provider.GetRequiredService<IAccountService>();

        var a = (await servers.AddAsync(ServerA)).Value!;
        var b = (await servers.AddAsync(ServerB)).Value!;

        Assert.True((await accounts.SignInThirdPartyAsync(a.Id, "Steve", "pw")).IsSuccess);
        Assert.True((await accounts.SignInThirdPartyAsync(b.Id, "Steve", "pw")).IsSuccess);
        Assert.Equal(2, (await accounts.GetAccountsAsync()).Count);

        var duplicate = await accounts.SignInThirdPartyAsync(a.Id, "Steve", "pw");
        Assert.False(duplicate.IsSuccess);
        Assert.Equal("account.duplicate", duplicate.Error?.Code);
    }

    [Fact]
    public async Task RemovingServerIsBlockedWhileItHasAccounts()
    {
        using var environment = new TestEnvironment();
        await using var provider = CreateProvider(environment, new StubAuthlibAuthentication());
        var servers = provider.GetRequiredService<IAuthServerService>();
        var accounts = provider.GetRequiredService<IAccountService>();

        var server = (await servers.AddAsync(ServerA)).Value!;
        await accounts.SignInThirdPartyAsync(server.Id, "Steve", "pw");

        var blocked = await servers.RemoveAsync(server.Id);
        Assert.False(blocked.IsSuccess);
        Assert.Equal("authserver.in_use", blocked.Error?.Code);
    }

    [Fact]
    public async Task ThirdPartySignInDoesNotTouchMicrosoftPath()
    {
        using var environment = new TestEnvironment();
        var microsoft = new StubMicrosoftAuthentication();
        await using var provider = CreateProvider(environment, new StubAuthlibAuthentication(), microsoft);
        var servers = provider.GetRequiredService<IAuthServerService>();
        var accounts = provider.GetRequiredService<IAccountService>();

        var server = (await servers.AddAsync(ServerA)).Value!;
        await accounts.SignInThirdPartyAsync(server.Id, "Steve", "pw");

        Assert.Equal(0, microsoft.SignInCalls);
        Assert.Equal(0, microsoft.RefreshCalls);
        var accountsList = await accounts.GetAccountsAsync();
        Assert.DoesNotContain(accountsList, account => account.Type == AccountType.Microsoft);
    }

    [Fact]
    public async Task EnsureFreshRefreshesOnlyWhenServerRejectsToken()
    {
        using var environment = new TestEnvironment();
        var stub = new StubAuthlibAuthentication { ValidateResult = true };
        await using var provider = CreateProvider(environment, stub);
        var servers = provider.GetRequiredService<IAuthServerService>();
        var accounts = provider.GetRequiredService<IAccountService>();

        var server = (await servers.AddAsync(ServerA)).Value!;
        await accounts.SignInThirdPartyAsync(server.Id, "Steve", "pw");

        var account = await accounts.GetDefaultAccountAsync();
        var valid = await accounts.EnsureThirdPartyFreshAsync(account!);
        Assert.True(valid.IsSuccess, valid.Error?.ToString());
        Assert.Equal(0, stub.RefreshCalls);
        Assert.Equal("ygg-access-Steve", valid.Value!.AccessToken);

        stub.ValidateResult = false;
        var refreshed = await accounts.EnsureThirdPartyFreshAsync(account!);
        Assert.True(refreshed.IsSuccess, refreshed.Error?.ToString());
        Assert.Equal(1, stub.RefreshCalls);
        Assert.Equal("refreshed-access", refreshed.Value!.AccessToken);

        var persisted = await accounts.GetDefaultAccountAsync();
        Assert.Equal("refreshed-access", persisted!.AccessToken);
    }

    [Fact]
    public async Task RegisterLinkIsUserSuppliedAndFallsBackToMetadata()
    {
        using var environment = new TestEnvironment();
        var stub = new StubAuthlibAuthentication { MetadataRegisterUrl = "https://auth-a.example.com/register" };
        await using var provider = CreateProvider(environment, stub);
        var servers = provider.GetRequiredService<IAuthServerService>();

        // 用户没填：使用元数据提供的注册链接。
        var defaults = (await servers.AddAsync(ServerA)).Value!;
        Assert.Equal("https://auth-a.example.com/register", defaults.RegisterUrl);

        // 用户填写：以用户输入为准。
        var custom = (await servers.AddAsync(ServerA, "https://auth-a.example.com/signup")).Value!;
        Assert.Equal("https://auth-a.example.com/signup", custom.RegisterUrl);

        // 账户设置里可以改地址与注册链接；服务器 ID 保持不变，账号引用不会失效。
        var updated = (await servers.UpdateAsync(custom.Id, ServerB, "https://auth-b.example.com/join")).Value!;
        Assert.Equal(custom.Id, updated.Id);
        Assert.Equal(ServerB, updated.Url);
        Assert.Equal("Server auth-b.example.com", updated.Name);
        Assert.Equal("https://auth-b.example.com/join", updated.RegisterUrl);

        // 地址已被另一台服务器占用时拒绝。
        var other = (await servers.AddAsync("https://auth-c.example.com/api/yggdrasil")).Value!;
        var clash = await servers.UpdateAsync(other.Id, ServerB, null);
        Assert.False(clash.IsSuccess);
        Assert.Equal("authserver.duplicate", clash.Error?.Code);
    }

    [Fact]
    public async Task ReauthenticateRefreshesCredentialsAndProfile()
    {
        using var environment = new TestEnvironment();
        var stub = new StubAuthlibAuthentication();
        await using var provider = CreateProvider(environment, stub);
        var servers = provider.GetRequiredService<IAuthServerService>();
        var accounts = provider.GetRequiredService<IAccountService>();

        var server = (await servers.AddAsync(ServerA)).Value!;
        var signedIn = await accounts.SignInThirdPartyAsync(server.Id, "Steve", "old-password");
        Assert.True(signedIn.IsSuccess, signedIn.Error?.ToString());

        // 服务器上的角色改名后，重新登录应同步到账户文档。
        stub.AuthenticatedName = "SteveRenamed";
        var again = await accounts.ReauthenticateThirdPartyAsync(signedIn.Value!.Id, "new-password");

        Assert.True(again.IsSuccess, again.Error?.ToString());
        Assert.Equal("SteveRenamed", again.Value!.Name);

        var persisted = await accounts.GetDefaultAccountAsync();
        Assert.Equal("SteveRenamed", persisted!.Name);
        Assert.Equal("ygg-access-Steve", persisted.AccessToken);
    }

    [Fact]
    public async Task ReauthenticateRejectsNonThirdPartyAccounts()
    {
        using var environment = new TestEnvironment();
        await using var provider = CreateProvider(environment, new StubAuthlibAuthentication());
        var accounts = provider.GetRequiredService<IAccountService>();

        var offline = await accounts.AddOfflineAsync("Steve");
        var result = await accounts.ReauthenticateThirdPartyAsync(offline.Value!.Id, "pw");

        Assert.False(result.IsSuccess);
        Assert.Equal("account.invalid_type", result.Error?.Code);
    }

    private static ServiceProvider CreateProvider(
        TestEnvironment environment,
        IAuthlibAuthenticationService authlib,
        IMicrosoftAuthenticationService? microsoft = null) =>
        environment.CreateServices(services =>
        {
            services.AddSingleton<ISecureCredentialStore, InMemoryCredentialStore>();
            services.AddSingleton(authlib);
            if (microsoft is not null)
            {
                services.AddSingleton(microsoft);
            }
        });

    private sealed class StubAuthlibAuthentication : IAuthlibAuthenticationService
    {
        public int RefreshCalls { get; private set; }

        public bool ValidateResult { get; set; } = true;

        /// <summary>模拟元数据里的注册链接（meta.links.register）。</summary>
        public string? MetadataRegisterUrl { get; set; }

        /// <summary>模拟服务器上的角色名（可用于测试改名同步）。</summary>
        public string? AuthenticatedName { get; set; }

        public Task<Result<AuthServerMetadata>> FetchMetadataAsync(string url, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<AuthServerMetadata>.Ok(new AuthServerMetadata
            {
                Url = url,
                ServerName = "Server " + new Uri(url).Host,
                RegisterUrl = MetadataRegisterUrl,
            }));

        public Task<Result<AuthlibSession>> AuthenticateAsync(
            AuthServer server,
            string username,
            string password,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<AuthlibSession>.Ok(new AuthlibSession
            {
                AccessToken = "ygg-access-" + username,
                ClientToken = "client-" + username,
                PlayerName = AuthenticatedName ?? username,
                Uuid = "01234567-89ab-cdef-0123-456789abcdef",
                AvailableProfiles = 1,
            }));

        public Task<Result<bool>> ValidateAsync(
            AuthServer server,
            string accessToken,
            string clientToken,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<bool>.Ok(ValidateResult));

        public Task<Result<AuthlibSession>> RefreshAsync(
            AuthServer server,
            string accessToken,
            string clientToken,
            CancellationToken cancellationToken = default)
        {
            RefreshCalls++;
            return Task.FromResult(Result<AuthlibSession>.Ok(new AuthlibSession
            {
                AccessToken = "refreshed-access",
                ClientToken = "refreshed-client",
                PlayerName = "Steve",
                Uuid = "01234567-89ab-cdef-0123-456789abcdef",
            }));
        }
    }

    private sealed class StubMicrosoftAuthentication : IMicrosoftAuthenticationService
    {
        public int SignInCalls { get; private set; }

        public int RefreshCalls { get; private set; }

        public Task<Result<Account>> SignInAsync(
            MicrosoftAuthenticationOptions options,
            Func<Uri, Task> openBrowser,
            CancellationToken cancellationToken = default)
        {
            SignInCalls++;
            throw new NotSupportedException("第三方登录不应进入 Microsoft 登录流程。");
        }

        public Task<Result<AccountCredentials>> RefreshAsync(
            MicrosoftAuthenticationOptions options,
            string microsoftRefreshToken,
            CancellationToken cancellationToken = default)
        {
            RefreshCalls++;
            throw new NotSupportedException("第三方登录不应刷新 Microsoft 凭据。");
        }
    }
}
