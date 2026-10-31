// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;

namespace Ecl.Auth;

public interface IAccountService
{
    Task<IReadOnlyList<Account>> GetAccountsAsync(CancellationToken cancellationToken = default);

    Task<Account?> GetDefaultAccountAsync(CancellationToken cancellationToken = default);

    Task<Result<Account>> AddOfflineAsync(string name, bool makeDefault = false, CancellationToken cancellationToken = default);

    Task<Result<Account>> AddAsync(Account account, bool makeDefault = false, CancellationToken cancellationToken = default);

    Task<Result<Account>> AddMicrosoftAsync(Account account, AccountCredentials credentials, bool makeDefault = false, CancellationToken cancellationToken = default);

    Task<Result<Account>> AddThirdPartyAsync(Account account, AccountCredentials credentials, bool makeDefault = false, CancellationToken cancellationToken = default);

    Task<Result> RemoveAsync(string accountId, CancellationToken cancellationToken = default);

    Task<Result> SetDefaultAsync(string accountId, CancellationToken cancellationToken = default);

    /// <summary>静默刷新 Microsoft 账户凭据（不打开浏览器）。</summary>
    Task<Result<Account>> RefreshAsync(
        string accountId,
        MicrosoftAuthenticationOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>当 Minecraft token 即将过期或已过期时静默刷新；否则原样返回。</summary>
    Task<Result<Account>> EnsureFreshAsync(
        Account account,
        MicrosoftAuthenticationOptions options,
        TimeSpan margin,
        CancellationToken cancellationToken = default);

    /// <summary>校验第三方账户令牌，必要时用 refresh token 换新；不打开浏览器。</summary>
    Task<Result<Account>> EnsureThirdPartyFreshAsync(Account account, CancellationToken cancellationToken = default);

    /// <summary>用用户名/密码登录第三方认证服务器并保存账号；密码不会持久化。</summary>
    Task<Result<Account>> SignInThirdPartyAsync(
        string authServerId,
        string username,
        string password,
        bool makeDefault = false,
        CancellationToken cancellationToken = default);

    /// <summary>用密码重新登录已有第三方账户，并同步服务器上的最新角色信息。</summary>
    Task<Result<Account>> ReauthenticateThirdPartyAsync(
        string accountId,
        string password,
        CancellationToken cancellationToken = default);
}
