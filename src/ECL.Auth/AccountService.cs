// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.RegularExpressions;
using Ecl.Core;
using Microsoft.Extensions.Logging;

namespace Ecl.Auth;

public sealed partial class AccountService : IAccountService
{
    private readonly IAccountStore _store;
    private readonly ISecureCredentialStore _credentials;
    private readonly ILogger<AccountService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AccountService(IAccountStore store, ISecureCredentialStore credentials, ILogger<AccountService> logger)
    {
        _store = store;
        _credentials = credentials;
        _logger = logger;
    }

    public async Task<IReadOnlyList<Account>> GetAccountsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _store.Load().Accounts.OrderBy(account => account.CreatedAt).ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Account?> GetDefaultAccountAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = _store.Load();
            var account = document.DefaultAccountId is not null
                ? document.Accounts.FirstOrDefault(item => item.Id == document.DefaultAccountId)
                : null;
            account ??= document.Accounts.FirstOrDefault();

            if (account is null || account.Type != AccountType.Microsoft)
            {
                return account;
            }

            var credentials = await _credentials.GetAsync(account.Id, cancellationToken).ConfigureAwait(false);
            if (!credentials.IsSuccess)
            {
                _logger.LogWarning("Microsoft 账户 {AccountId} 缺少安全凭据：{Code}", account.Id, credentials.Error?.Code);
                return account;
            }

            return account with
            {
                AccessToken = credentials.Value!.MinecraftAccessToken,
                RefreshToken = credentials.Value.MicrosoftRefreshToken,
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Result<Account>> AddOfflineAsync(string name, bool makeDefault = false, CancellationToken cancellationToken = default)
    {
        name = name.Trim();
        if (!UsernameRegex().IsMatch(name))
        {
            return Result<Account>.Fail("account.invalid_name", "用户名需为 3-16 位字母、数字或下划线。");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = _store.Load();
            if (document.Accounts.Any(account => string.Equals(account.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                return Result<Account>.Fail("account.duplicate", $"已存在同名账户：{name}");
            }

            var account = new Account
            {
                Id = Guid.NewGuid().ToString("N"),
                Type = AccountType.Offline,
                Name = name,
                Uuid = OfflineUuid.Create(name),
                CreatedAt = DateTimeOffset.UtcNow,
            };

            return await AddCoreAsync(document, account, makeDefault, null, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<Result<Account>> AddAsync(Account account, bool makeDefault = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (account.Type == AccountType.Microsoft)
        {
            if (string.IsNullOrWhiteSpace(account.AccessToken))
            {
                return Task.FromResult(Result<Account>.Fail("account.credentials_missing", "Microsoft 账户缺少 Minecraft 登录凭据。"));
            }

            return AddMicrosoftAsync(
                account,
                new AccountCredentials
                {
                    MinecraftAccessToken = account.AccessToken,
                    MicrosoftRefreshToken = account.RefreshToken,
                },
                makeDefault,
                cancellationToken);
        }

        return AddNonMicrosoftAsync(account, makeDefault, cancellationToken);
    }

    public async Task<Result<Account>> AddMicrosoftAsync(Account account, AccountCredentials credentials, bool makeDefault = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(credentials);

        if (account.Type != AccountType.Microsoft)
        {
            return Result<Account>.Fail("account.invalid_type", "只能为 Microsoft 账户保存 Microsoft 凭据。");
        }

        if (string.IsNullOrWhiteSpace(credentials.MinecraftAccessToken))
        {
            return Result<Account>.Fail("account.credentials_missing", "Minecraft access token 为空。");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = _store.Load();
            if (document.Accounts.Any(existing =>
                    string.Equals(existing.Name, account.Name, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(existing.Id, account.Id, StringComparison.Ordinal)))
            {
                return Result<Account>.Fail("account.duplicate", $"已存在同名或同 ID 账户：{account.Name}");
            }

            var saveCredentials = await _credentials.SaveAsync(account.Id, credentials, cancellationToken).ConfigureAwait(false);
            if (!saveCredentials.IsSuccess)
            {
                return Result<Account>.Fail(saveCredentials.Error!);
            }

            try
            {
                var result = await AddCoreAsync(document, account with
                {
                    AccessToken = null,
                    RefreshToken = null,
                }, makeDefault, null, cancellationToken).ConfigureAwait(false);
                if (!result.IsSuccess)
                {
                    await _credentials.RemoveAsync(account.Id, cancellationToken).ConfigureAwait(false);
                }

                return result;
            }
            catch
            {
                await _credentials.RemoveAsync(account.Id, cancellationToken).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Result<Account>> AddNonMicrosoftAsync(Account account, bool makeDefault, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(account.Id) || string.IsNullOrWhiteSpace(account.Name) || string.IsNullOrWhiteSpace(account.Uuid))
        {
            return Result<Account>.Fail("account.invalid", "账户资料不完整。");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = _store.Load();
            if (document.Accounts.Any(existing =>
                    string.Equals(existing.Name, account.Name, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(existing.Id, account.Id, StringComparison.Ordinal)))
            {
                return Result<Account>.Fail("account.duplicate", $"已存在同名或同 ID 账户：{account.Name}");
            }

            return await AddCoreAsync(document, account, makeDefault, null, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task<Result<Account>> AddCoreAsync(
        AccountsDocument document,
        Account account,
        bool makeDefault,
        AccountCredentials? credentials,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        document.Accounts.Add(account);
        if (makeDefault || document.DefaultAccountId is null)
        {
            document.DefaultAccountId = account.Id;
        }

        _store.Save(document);
        _logger.LogInformation("已添加 {Type} 账户 {Name}（{Uuid}）。", account.Type, account.Name, account.Uuid);
        return Task.FromResult(Result<Account>.Ok(account));
    }

    public async Task<Result> RemoveAsync(string accountId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = _store.Load();
            var account = document.Accounts.FirstOrDefault(item => item.Id == accountId);
            if (account is null)
            {
                return Result.Fail("account.not_found", "账户不存在。");
            }

            if (account.Type == AccountType.Microsoft)
            {
                var removeCredentials = await _credentials.RemoveAsync(accountId, cancellationToken).ConfigureAwait(false);
                if (!removeCredentials.IsSuccess)
                {
                    return removeCredentials;
                }
            }

            document.Accounts.Remove(account);
            if (document.DefaultAccountId == accountId)
            {
                document.DefaultAccountId = document.Accounts.FirstOrDefault()?.Id;
            }

            _store.Save(document);
            _logger.LogInformation("已删除账户 {Name}。", account.Name);
            return Result.Ok();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Result> SetDefaultAsync(string accountId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = _store.Load();
            if (document.Accounts.All(account => account.Id != accountId))
            {
                return Result.Fail("account.not_found", "账户不存在。");
            }

            document.DefaultAccountId = accountId;
            _store.Save(document);
            return Result.Ok();
        }
        finally
        {
            _gate.Release();
        }
    }

    [GeneratedRegex("^[A-Za-z0-9_]{3,16}$", RegexOptions.CultureInvariant)]
    private static partial Regex UsernameRegex();
}
