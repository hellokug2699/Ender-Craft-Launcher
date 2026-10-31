// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Auth;
using Ecl.Core;
using Ecl.Minecraft.Launch;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecl.Tests.Auth;

/// <summary>
/// 启动前的续期判定：token 仍新鲜时必须零网络请求；即将过期或未知时才静默刷新。
/// </summary>
public sealed class AccountRefreshTests
{
    [Fact]
    public async Task SkipsRefreshWhenTokenIsStillValid()
    {
        var stub = new StubAuthenticationService();
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices(services =>
        {
            services.AddSingleton<ISecureCredentialStore, InMemoryCredentialStore>();
            services.AddSingleton<IMicrosoftAuthenticationService>(stub);
        });

        var accounts = provider.GetRequiredService<IAccountService>();
        await AddAccountAsync(accounts, DateTimeOffset.UtcNow.AddHours(1));

        var account = await accounts.GetDefaultAccountAsync();
        var result = await accounts.EnsureFreshAsync(account!, Options(), LaunchService.RefreshMargin);

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Equal("mc-old", result.Value!.AccessToken);
        Assert.Equal(0, stub.RefreshCalls);
    }

    [Fact]
    public async Task RefreshesWhenTokenIsNearExpiryAndPersistsNewCredentials()
    {
        var stub = new StubAuthenticationService();
        var store = new InMemoryCredentialStore();
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices(services =>
        {
            services.AddSingleton<ISecureCredentialStore>(store);
            services.AddSingleton<IMicrosoftAuthenticationService>(stub);
        });

        var accounts = provider.GetRequiredService<IAccountService>();
        var added = await AddAccountAsync(accounts, DateTimeOffset.UtcNow.AddMinutes(2));

        var account = await accounts.GetDefaultAccountAsync();
        var result = await accounts.EnsureFreshAsync(account!, Options(), LaunchService.RefreshMargin);

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Equal(1, stub.RefreshCalls);
        Assert.Equal("mc-new", result.Value!.AccessToken);
        Assert.Equal("ms-new", result.Value.RefreshToken);
        Assert.Equal("mc-new", store.Saved[added.Id].MinecraftAccessToken);
        Assert.Equal("ms-new", store.Saved[added.Id].MicrosoftRefreshToken);
    }

    [Fact]
    public async Task RefreshesWhenExpiryIsUnknown()
    {
        var stub = new StubAuthenticationService();
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices(services =>
        {
            services.AddSingleton<ISecureCredentialStore, InMemoryCredentialStore>();
            services.AddSingleton<IMicrosoftAuthenticationService>(stub);
        });

        var accounts = provider.GetRequiredService<IAccountService>();
        await AddAccountAsync(accounts, expiresAt: null);

        var account = await accounts.GetDefaultAccountAsync();
        var result = await accounts.EnsureFreshAsync(account!, Options(), LaunchService.RefreshMargin);

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Equal(1, stub.RefreshCalls);
    }

    [Theory]
    [InlineData(180, false)]
    [InlineData(45, false)]
    [InlineData(20, true)]
    [InlineData(5, true)]
    public async Task RefreshDecisionDependsOnRemainingLifetime(int remainingMinutes, bool expectRefresh)
    {
        var stub = new StubAuthenticationService();
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices(services =>
        {
            services.AddSingleton<ISecureCredentialStore, InMemoryCredentialStore>();
            services.AddSingleton<IMicrosoftAuthenticationService>(stub);
        });

        var accounts = provider.GetRequiredService<IAccountService>();
        await AddAccountAsync(accounts, DateTimeOffset.UtcNow.AddMinutes(remainingMinutes));

        var account = await accounts.GetDefaultAccountAsync();
        var result = await accounts.EnsureFreshAsync(account!, Options(), LaunchService.RefreshMargin);

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Equal(expectRefresh ? 1 : 0, stub.RefreshCalls);
    }

    private static MicrosoftAuthenticationOptions Options() =>
        new() { ClientId = "a1e97b44-9c00-4a60-8e32-1119fefe7aab" };

    private static async Task<Account> AddAccountAsync(IAccountService accounts, DateTimeOffset? expiresAt)
    {
        var account = new Account
        {
            Id = Guid.NewGuid().ToString("N"),
            Type = AccountType.Microsoft,
            Name = "Steve",
            Uuid = "0123456789abcdef0123456789abcdef",
            AccessToken = "mc-old",
            RefreshToken = "ms-old",
            MinecraftTokenExpiresAt = expiresAt,
        };

        var result = await accounts.AddMicrosoftAsync(
            account,
            new AccountCredentials
            {
                MinecraftAccessToken = "mc-old",
                MicrosoftRefreshToken = "ms-old",
                MinecraftTokenExpiresAt = expiresAt,
            },
            makeDefault: true);
        Assert.True(result.IsSuccess, result.Error?.ToString());
        return result.Value!;
    }

    private sealed class StubAuthenticationService : IMicrosoftAuthenticationService
    {
        public int RefreshCalls { get; private set; }

        public Task<Result<Account>> SignInAsync(
            MicrosoftAuthenticationOptions options,
            Func<Uri, Task> openBrowser,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("刷新测试不应触发交互式登录。");

        public Task<Result<AccountCredentials>> RefreshAsync(
            MicrosoftAuthenticationOptions options,
            string microsoftRefreshToken,
            CancellationToken cancellationToken = default)
        {
            RefreshCalls++;
            return Task.FromResult(Result<AccountCredentials>.Ok(new AccountCredentials
            {
                MinecraftAccessToken = "mc-new",
                MicrosoftRefreshToken = "ms-new",
                MinecraftTokenExpiresAt = DateTimeOffset.UtcNow.AddHours(24),
            }));
        }
    }

    private sealed class InMemoryCredentialStore : ISecureCredentialStore
    {
        public Dictionary<string, AccountCredentials> Saved { get; } = [];

        public Task<Result> SaveAsync(string accountId, AccountCredentials credentials, CancellationToken cancellationToken = default)
        {
            Saved[accountId] = credentials;
            return Task.FromResult(Result.Ok());
        }

        public Task<Result<AccountCredentials>> GetAsync(string accountId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Saved.TryGetValue(accountId, out var credentials)
                ? Result<AccountCredentials>.Ok(credentials)
                : Result<AccountCredentials>.Fail("auth.credentials_missing", "missing"));

        public Task<Result> RemoveAsync(string accountId, CancellationToken cancellationToken = default)
        {
            Saved.Remove(accountId);
            return Task.FromResult(Result.Ok());
        }
    }
}
