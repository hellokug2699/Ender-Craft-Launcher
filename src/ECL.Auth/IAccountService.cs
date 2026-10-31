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

    Task<Result> RemoveAsync(string accountId, CancellationToken cancellationToken = default);

    Task<Result> SetDefaultAsync(string accountId, CancellationToken cancellationToken = default);
}
