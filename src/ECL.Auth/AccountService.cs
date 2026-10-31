// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using System.Text.RegularExpressions;
using Ecl.Core;
using Microsoft.Extensions.Logging;

namespace Ecl.Auth;

public sealed partial class AccountService : IAccountService
{
    private readonly IAccountStore _store;
    private readonly ISecureCredentialStore _credentials;
    private readonly IMicrosoftAuthenticationService _microsoftAuthentication;
    private readonly IAuthlibAuthenticationService _authlibAuthentication;
    private readonly IAuthServerStore _authServers;
    private readonly ILogger<AccountService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AccountService(
        IAccountStore store,
        ISecureCredentialStore credentials,
        IMicrosoftAuthenticationService microsoftAuthentication,
        IAuthlibAuthenticationService authlibAuthentication,
        IAuthServerStore authServers,
        ILogger<AccountService> logger)
    {
        _store = store;
        _credentials = credentials;
        _microsoftAuthentication = microsoftAuthentication;
        _authlibAuthentication = authlibAuthentication;
        _authServers = authServers;
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

            if (account is null || account.Type == AccountType.Offline)
            {
                return account;
            }

            var credentials = await _credentials.GetAsync(account.Id, cancellationToken).ConfigureAwait(false);
            if (!credentials.IsSuccess)
            {
                _logger.LogWarning("账户 {AccountId} 缺少安全凭据：{Code}", account.Id, credentials.Error?.Code);
                return account;
            }

            return account with
            {
                AccessToken = credentials.Value!.MinecraftAccessToken,
                RefreshToken = credentials.Value.MicrosoftRefreshToken,
                ClientToken = credentials.Value.AuthlibClientToken,
                MinecraftTokenExpiresAt = credentials.Value.MinecraftTokenExpiresAt,
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
            var account = new Account
            {
                Id = Guid.NewGuid().ToString("N"),
                Type = AccountType.Offline,
                Name = name,
                Uuid = OfflineUuid.Create(name),
                CreatedAt = DateTimeOffset.UtcNow,
            };

            if (IsDuplicate(document.Accounts, account))
            {
                return Result<Account>.Fail("account.duplicate", $"已存在同名离线账户：{name}");
            }

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
                    MinecraftTokenExpiresAt = account.MinecraftTokenExpiresAt,
                },
                makeDefault,
                cancellationToken);
        }

        if (account.Type == AccountType.ThirdParty)
        {
            if (string.IsNullOrWhiteSpace(account.AccessToken))
            {
                return Task.FromResult(Result<Account>.Fail("account.credentials_missing", "第三方账户缺少登录凭据。"));
            }

            return AddThirdPartyAsync(
                account,
                new AccountCredentials
                {
                    MinecraftAccessToken = account.AccessToken,
                    AuthlibClientToken = account.ClientToken,
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
            if (IsDuplicate(document.Accounts, account))
            {
                return Result<Account>.Fail("account.duplicate", $"已存在同名同类型账户：{account.Name}");
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

    public async Task<Result<Account>> AddThirdPartyAsync(Account account, AccountCredentials credentials, bool makeDefault = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(credentials);

        if (account.Type != AccountType.ThirdParty)
        {
            return Result<Account>.Fail("account.invalid_type", "只能为第三方账户保存第三方认证凭据。");
        }

        if (string.IsNullOrWhiteSpace(account.AuthServerId))
        {
            return Result<Account>.Fail("account.invalid", "第三方账户必须关联认证服务器。");
        }

        if (string.IsNullOrWhiteSpace(credentials.MinecraftAccessToken))
        {
            return Result<Account>.Fail("account.credentials_missing", "第三方 access token 为空。");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = _store.Load();
            if (IsDuplicate(document.Accounts, account))
            {
                return Result<Account>.Fail(
                    "account.duplicate",
                    $"该认证服务器上已存在同名账户：{account.Name}");
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
                    ClientToken = null,
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

    private async Task<Result<Account>> AddNonMicrosoftAsync(Account account, bool makeDefault, CancellationToken cancellationToken)    {
        if (string.IsNullOrWhiteSpace(account.Id) || string.IsNullOrWhiteSpace(account.Name) || string.IsNullOrWhiteSpace(account.Uuid))
        {
            return Result<Account>.Fail("account.invalid", "账户资料不完整。");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = _store.Load();
            if (IsDuplicate(document.Accounts, account))
            {
                return Result<Account>.Fail("account.duplicate", $"已存在同名同类型账户：{account.Name}");
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

            if (account.Type != AccountType.Offline)
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

    public async Task<Result<Account>> RefreshAsync(
        string accountId,
        MicrosoftAuthenticationOptions options,
        CancellationToken cancellationToken = default)
    {
        Account account;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = _store.Load();
            var found = document.Accounts.FirstOrDefault(item => item.Id == accountId);
            if (found is null)
            {
                return Result<Account>.Fail("account.not_found", "账户不存在。");
            }

            if (found.Type != AccountType.Microsoft)
            {
                return Result<Account>.Fail("account.invalid_type", "只有 Microsoft 账户需要刷新凭据。");
            }

            var credentials = await _credentials.GetAsync(accountId, cancellationToken).ConfigureAwait(false);
            if (!credentials.IsSuccess)
            {
                return Result<Account>.Fail(credentials.Error!);
            }

            account = found with
            {
                AccessToken = credentials.Value!.MinecraftAccessToken,
                RefreshToken = credentials.Value.MicrosoftRefreshToken,
                MinecraftTokenExpiresAt = credentials.Value.MinecraftTokenExpiresAt,
            };
        }
        finally
        {
            _gate.Release();
        }

        // 网络请求在锁外执行，避免刷新期间阻塞其他账户读写操作。
        return await RefreshCoreAsync(account, options, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<Account>> EnsureFreshAsync(
        Account account,
        MicrosoftAuthenticationOptions options,
        TimeSpan margin,
        CancellationToken cancellationToken = default)
    {
        if (account.Type != AccountType.Microsoft)
        {
            return Result<Account>.Ok(account);
        }

        var now = DateTimeOffset.UtcNow;
        // 优先使用凭据里记录的有效期；旧凭据没有记录时，从 token 自身的 exp 声明推断，
        // 避免“不知道有效期就盲目刷新”。
        var expiry = account.MinecraftTokenExpiresAt ?? TryReadTokenExpiry(account.AccessToken);

        if (expiry is { } valid && valid - now > margin)
        {
            _logger.LogInformation(
                "Microsoft 账户 {AccountId} 的 Minecraft token 仍有效（剩余 {Remaining}），复用现有凭据，不执行刷新。",
                account.Id,
                DescribeRemaining(valid - now));
            return Result<Account>.Ok(account with { MinecraftTokenExpiresAt = valid });
        }

        if (expiry is null)
        {
            _logger.LogInformation(
                "Microsoft 账户 {AccountId} 的 Minecraft token 有效期未知，执行一次静默刷新以确认。",
                account.Id);
        }
        else
        {
            _logger.LogInformation(
                "Microsoft 账户 {AccountId} 的 Minecraft token 将在 {Remaining} 后过期（提前 {Margin} 刷新），执行静默刷新。",
                account.Id,
                DescribeRemaining(expiry.Value - now),
                DescribeRemaining(margin));
        }

        var refreshed = await RefreshCoreAsync(account, options, cancellationToken).ConfigureAwait(false);
        if (refreshed.IsSuccess)
        {
            return refreshed;
        }

        // 刷新失败但旧 token 仍未过期时继续使用旧 token；只有已过期才阻断启动。
        if (account.MinecraftTokenExpiresAt is { } stillValid &&
            stillValid > now &&
            !string.IsNullOrWhiteSpace(account.AccessToken))
        {
            return Result<Account>.Ok(account);
        }

        return refreshed;
    }

    /// <summary>
    /// 第三方账户没有可靠的“过期时间”，因此启动前向认证服务器校验一次令牌：
    /// 有效则复用；被拒绝则用 refresh 换新；仍失败才要求重新登录。
    /// </summary>
    public async Task<Result<Account>> EnsureThirdPartyFreshAsync(Account account, CancellationToken cancellationToken = default)
    {
        if (account.Type != AccountType.ThirdParty)
        {
            return Result<Account>.Ok(account);
        }

        var server = await ResolveAuthServerAsync(account.AuthServerId ?? string.Empty, cancellationToken).ConfigureAwait(false);
        if (!server.IsSuccess)
        {
            return Result<Account>.Fail(server.Error!);
        }

        if (string.IsNullOrWhiteSpace(account.AccessToken))
        {
            return Result<Account>.Fail("auth.thirdparty_relogin", "第三方账户缺少登录凭据，请重新登录。");
        }

        var validated = await _authlibAuthentication
            .ValidateAsync(server.Value!, account.AccessToken, account.ClientToken ?? string.Empty, cancellationToken)
            .ConfigureAwait(false);
        if (validated.IsSuccess && validated.Value)
        {
            _logger.LogInformation(
                "第三方账户 {AccountId} 的令牌仍然有效（{Server}），复用现有凭据，不执行刷新。",
                account.Id,
                server.Value!.Name);
            return Result<Account>.Ok(account);
        }

        if (!validated.IsSuccess)
        {
            // 校验请求本身失败（例如服务器暂时不可达）：保留现有令牌，避免因网络问题挡住启动。
            _logger.LogWarning(
                "无法校验第三方账户 {AccountId} 的令牌（{Code}），继续使用现有凭据。",
                account.Id,
                validated.Error?.Code);
            return Result<Account>.Ok(account);
        }

        var refreshed = await _authlibAuthentication
            .RefreshAsync(server.Value!, account.AccessToken, account.ClientToken ?? string.Empty, cancellationToken)
            .ConfigureAwait(false);
        if (!refreshed.IsSuccess)
        {
            _logger.LogWarning("刷新第三方账户 {AccountId} 凭据失败：{Code}", account.Id, refreshed.Error?.Code);
            return Result<Account>.Fail(
                "auth.thirdparty_relogin",
                $"第三方账户登录已失效，请在启动器中重新登录：{refreshed.Error?.Message}");
        }

        var credentials = new AccountCredentials
        {
            MinecraftAccessToken = refreshed.Value!.AccessToken,
            AuthlibClientToken = refreshed.Value.ClientToken,
        };

        var save = await _credentials.SaveAsync(account.Id, credentials, cancellationToken).ConfigureAwait(false);
        if (!save.IsSuccess)
        {
            return Result<Account>.Fail(save.Error!);
        }

        _logger.LogInformation("已静默刷新第三方账户 {AccountId} 的凭据。", account.Id);
        return Result<Account>.Ok(account with
        {
            AccessToken = credentials.MinecraftAccessToken,
            ClientToken = credentials.AuthlibClientToken,
        });
    }

    public async Task<Result<Account>> SignInThirdPartyAsync(
        string authServerId,
        string username,
        string password,
        bool makeDefault = false,
        CancellationToken cancellationToken = default)
    {
        var server = await ResolveAuthServerAsync(authServerId, cancellationToken).ConfigureAwait(false);
        if (!server.IsSuccess)
        {
            return Result<Account>.Fail(server.Error!);
        }

        var session = await _authlibAuthentication
            .AuthenticateAsync(server.Value!, username, password, cancellationToken)
            .ConfigureAwait(false);
        if (!session.IsSuccess)
        {
            return Result<Account>.Fail(session.Error!);
        }

        var value = session.Value!;
        var account = new Account
        {
            Id = Guid.NewGuid().ToString("N"),
            Type = AccountType.ThirdParty,
            Name = value.PlayerName,
            Uuid = value.Uuid,
            AuthServerId = server.Value!.Id,
            AccessToken = value.AccessToken,
            ClientToken = value.ClientToken,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        var added = await AddThirdPartyAsync(
            account,
            new AccountCredentials
            {
                MinecraftAccessToken = value.AccessToken,
                AuthlibClientToken = value.ClientToken,
            },
            makeDefault,
            cancellationToken).ConfigureAwait(false);
        if (added.IsSuccess)
        {
            _logger.LogInformation(
                "第三方账户登录成功：{Name}（{Uuid}），认证服务器 {Server}。",
                added.Value!.Name,
                added.Value.Uuid,
                server.Value!.Name);
        }

        return added;
    }

    private async Task<Result<AuthServer>> ResolveAuthServerAsync(string authServerId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(authServerId))
        {
            return Result<AuthServer>.Fail("account.invalid", "第三方账户缺少认证服务器信息。");
        }

        var document = _authServers.Load();
        var server = document.Servers.FirstOrDefault(item =>
            string.Equals(item.Id, authServerId, StringComparison.Ordinal));
        return server is null
            ? Result<AuthServer>.Fail("authserver.not_found", $"认证服务器不存在：{authServerId}")
            : Result<AuthServer>.Ok(server);
    }

    private async Task<Result<Account>> RefreshCoreAsync(
        Account account,
        MicrosoftAuthenticationOptions options,
        CancellationToken cancellationToken)
    {
        var refreshed = await _microsoftAuthentication
            .RefreshAsync(options, account.RefreshToken ?? string.Empty, cancellationToken)
            .ConfigureAwait(false);
        if (!refreshed.IsSuccess)
        {
            _logger.LogWarning("刷新 Microsoft 账户 {AccountId} 凭据失败：{Code}", account.Id, refreshed.Error?.Code);
            return Result<Account>.Fail(refreshed.Error!);
        }

        var save = await _credentials.SaveAsync(account.Id, refreshed.Value!, cancellationToken).ConfigureAwait(false);
        if (!save.IsSuccess)
        {
            return Result<Account>.Fail(save.Error!);
        }

        _logger.LogInformation("已静默刷新 Microsoft 账户 {AccountId} 的凭据。", account.Id);
        return Result<Account>.Ok(account with
        {
            AccessToken = refreshed.Value!.MinecraftAccessToken,
            RefreshToken = refreshed.Value.MicrosoftRefreshToken,
            MinecraftTokenExpiresAt = refreshed.Value.MinecraftTokenExpiresAt,
        });
    }

    [GeneratedRegex("^[A-Za-z0-9_]{3,16}$", RegexOptions.CultureInvariant)]
    private static partial Regex UsernameRegex();

    /// <summary>
    /// 同一个 Minecraft 名字既可能对应正版账户，也可能对应同名的离线账户或另一台认证服务器上的账号，
    /// 因此只有“同 ID”或“同名且同类型且同认证服务器”才算重复。
    /// </summary>
    private static bool IsDuplicate(IEnumerable<Account> accounts, Account candidate) =>
        accounts.Any(existing =>
            string.Equals(existing.Id, candidate.Id, StringComparison.Ordinal) ||
            (existing.Type == candidate.Type &&
             string.Equals(existing.Name, candidate.Name, StringComparison.OrdinalIgnoreCase) &&
             string.Equals(existing.AuthServerId, candidate.AuthServerId, StringComparison.Ordinal)));

    /// <summary>旧凭据没有记录有效期时，从 Minecraft token 的 <c>exp</c> 声明推断。</summary>
    private static DateTimeOffset? TryReadTokenExpiry(string? accessToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return null;
        }

        var segments = accessToken.Split('.');
        if (segments.Length < 2)
        {
            return null;
        }

        try
        {
            var payload = Base64UrlDecode(segments[1]);
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                : null;
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        var padding = (4 - (normalized.Length % 4)) % 4;
        return Convert.FromBase64String(normalized.PadRight(normalized.Length + padding, '='));
    }

    private static string DescribeRemaining(TimeSpan remaining) =>
        remaining <= TimeSpan.Zero
            ? "已过期"
            : $"{(int)remaining.TotalHours} 小时 {remaining.Minutes} 分钟";
}
