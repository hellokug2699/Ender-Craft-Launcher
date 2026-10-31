// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Auth;
using Ecl.Core;

namespace Ecl.Tests.Auth;

/// <summary>测试用凭据存储：避免在 CI 上依赖 Windows DPAPI。</summary>
internal sealed class InMemoryCredentialStore : ISecureCredentialStore
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
