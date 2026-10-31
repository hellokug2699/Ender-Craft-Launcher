// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;

namespace Ecl.Auth;

public interface ISecureCredentialStore
{
    Task<Result> SaveAsync(string accountId, AccountCredentials credentials, CancellationToken cancellationToken = default);

    Task<Result<AccountCredentials>> GetAsync(string accountId, CancellationToken cancellationToken = default);

    Task<Result> RemoveAsync(string accountId, CancellationToken cancellationToken = default);
}
