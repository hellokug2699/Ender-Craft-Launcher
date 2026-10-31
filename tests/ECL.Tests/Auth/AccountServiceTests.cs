// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Auth;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecl.Tests.Auth;

public sealed class OfflineUuidTests
{
    [Theory]
    [InlineData("Notch", "b50ad385-829d-3141-a216-7e7d7539ba7f")]
    [InlineData("Player", "a01e3843-e521-3998-958a-f459800e4d11")]
    public void MatchesJavaOfflineUuid(string name, string expected)
    {
        Assert.Equal(expected, OfflineUuid.Create(name));
    }

    [Fact]
    public void IsCaseSensitive()
    {
        Assert.NotEqual(OfflineUuid.Create("notch"), OfflineUuid.Create("Notch"));
    }
}

public sealed class AccountServiceTests
{
    [Fact]
    public async Task AddListAndRemoveOfflineAccount()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var accounts = provider.GetRequiredService<IAccountService>();

        var added = await accounts.AddOfflineAsync("Notch");
        Assert.True(added.IsSuccess);
        Assert.Equal("b50ad385-829d-3141-a216-7e7d7539ba7f", added.Value!.Uuid);

        var defaultAccount = await accounts.GetDefaultAccountAsync();
        Assert.NotNull(defaultAccount);
        Assert.Equal(added.Value.Id, defaultAccount!.Id);

        var duplicate = await accounts.AddOfflineAsync("notch");
        Assert.False(duplicate.IsSuccess);
        Assert.Equal("account.duplicate", duplicate.Error?.Code);

        var invalid = await accounts.AddOfflineAsync("a");
        Assert.False(invalid.IsSuccess);

        var list = await accounts.GetAccountsAsync();
        Assert.Single(list);

        var removed = await accounts.RemoveAsync(added.Value.Id);
        Assert.True(removed.IsSuccess);
        Assert.Empty(await accounts.GetAccountsAsync());
        Assert.Null(await accounts.GetDefaultAccountAsync());
    }

    [Fact]
    public async Task AllowsSameNameForDifferentAccountTypes()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var accounts = provider.GetRequiredService<IAccountService>();

        var offline = await accounts.AddOfflineAsync("hellokug");
        Assert.True(offline.IsSuccess);

        var thirdParty = await accounts.AddAsync(new Account
        {
            Id = Guid.NewGuid().ToString("N"),
            Type = AccountType.ThirdParty,
            Name = "hellokug",
            Uuid = "0123456789abcdef0123456789abcdef",
        });
        Assert.True(thirdParty.IsSuccess, thirdParty.Error?.ToString());
        Assert.Equal(2, (await accounts.GetAccountsAsync()).Count);

        var duplicate = await accounts.AddAsync(new Account
        {
            Id = Guid.NewGuid().ToString("N"),
            Type = AccountType.ThirdParty,
            Name = "HELLOKUG",
            Uuid = "fedcba9876543210fedcba9876543210",
        });
        Assert.False(duplicate.IsSuccess);
        Assert.Equal("account.duplicate", duplicate.Error?.Code);
    }

    [Fact]
    public async Task AccountsPersistAcrossServiceInstances()
    {
        using var environment = new TestEnvironment();

        await using (var provider = environment.CreateServices())
        {
            await provider.GetRequiredService<IAccountService>().AddOfflineAsync("Steve");
        }

        await using var secondProvider = environment.CreateServices();
        var accounts = await secondProvider.GetRequiredService<IAccountService>().GetAccountsAsync();
        Assert.Single(accounts);
        Assert.Equal("Steve", accounts[0].Name);
    }
}
