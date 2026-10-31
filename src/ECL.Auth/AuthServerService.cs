// SPDX-License-Identifier: GPL-3.0-or-later

using System.Security.Cryptography;
using System.Text;
using Ecl.Core;
using Microsoft.Extensions.Logging;

namespace Ecl.Auth;

/// <summary>
/// 认证服务器的一等对象管理：添加时校验元数据，删除时保护仍被账号引用的服务器。
/// </summary>
public interface IAuthServerService
{
    Task<IReadOnlyList<AuthServer>> GetServersAsync(CancellationToken cancellationToken = default);

    Task<AuthServer?> GetAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>按地址或 ID 查找，供 CLI 的宽松输入使用。</summary>
    Task<Result<AuthServer>> ResolveAsync(string idOrUrl, CancellationToken cancellationToken = default);

    /// <summary>
    /// 添加认证服务器。地址必填；注册链接可选，留空时使用元数据提供的注册链接。
    /// </summary>
    Task<Result<AuthServer>> AddAsync(string url, string? registerUrl = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// 只校验地址并读取元数据，不写入存储。供“修改服务器地址前先验证”等需要
    /// 先完成网络确认、再提交本地修改的流程使用。
    /// </summary>
    Task<Result<AuthServerMetadata>> ValidateAsync(string url, CancellationToken cancellationToken = default);

    /// <summary>
    /// 修改认证服务器的地址与注册链接（供“账户设置”使用）。
    /// 服务器 ID 保持不变，已有的账号引用不会失效。
    /// </summary>
    Task<Result<AuthServer>> UpdateAsync(
        string id,
        string? url,
        string? registerUrl,
        CancellationToken cancellationToken = default);

    Task<Result> RemoveAsync(string id, CancellationToken cancellationToken = default);
}

public sealed class AuthServerService(
    IAuthServerStore store,
    IAccountStore accounts,
    IAuthlibAuthenticationService authentication,
    ILogger<AuthServerService> logger) : IAuthServerService
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IReadOnlyList<AuthServer>> GetServersAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return store.Load().Servers.OrderBy(server => server.AddedAt).ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AuthServer?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return store.Load().Servers.FirstOrDefault(server =>
                string.Equals(server.Id, id, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Result<AuthServer>> ResolveAsync(string idOrUrl, CancellationToken cancellationToken = default)
    {
        var servers = await GetServersAsync(cancellationToken).ConfigureAwait(false);

        var byId = servers.FirstOrDefault(server =>
            string.Equals(server.Id, idOrUrl, StringComparison.OrdinalIgnoreCase));
        if (byId is not null)
        {
            return Result<AuthServer>.Ok(byId);
        }

        if (AuthlibAuthenticationService.TryNormalizeUrl(idOrUrl, out var normalized))
        {
            var byUrl = servers.FirstOrDefault(server =>
                string.Equals(server.Url, normalized, StringComparison.OrdinalIgnoreCase));
            if (byUrl is not null)
            {
                return Result<AuthServer>.Ok(byUrl);
            }
        }

        return Result<AuthServer>.Fail("authserver.not_found", $"认证服务器不存在：{idOrUrl}");
    }

    public async Task<Result<AuthServerMetadata>> ValidateAsync(
        string url,
        CancellationToken cancellationToken = default)
    {
        if (!AuthlibAuthenticationService.TryNormalizeUrl(url, out var normalized))
        {
            return Result<AuthServerMetadata>.Fail(
                "authserver.invalid_url",
                AuthlibAuthenticationService.HttpsRequiredMessage);
        }

        return await authentication.FetchMetadataAsync(normalized, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<AuthServer>> AddAsync(
        string url,
        string? registerUrl = null,
        CancellationToken cancellationToken = default)
    {
        if (!AuthlibAuthenticationService.TryNormalizeUrl(url, out var normalized))
        {
            return Result<AuthServer>.Fail(
                "authserver.invalid_url",
                AuthlibAuthenticationService.HttpsRequiredMessage);
        }

        var requestedRegisterUrl = NormalizeRegisterUrl(registerUrl);

        // 已存在时不需要访问网络；注册链接变更也在锁内完成。
        var existing = (await GetServersAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(server =>
                string.Equals(server.Url, normalized, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return await SaveRegisterUrlAsync(existing, requestedRegisterUrl, cancellationToken).ConfigureAwait(false);
        }

        // 网络元数据请求放在锁外，避免慢服务器阻塞其它认证服务器操作。
        var metadata = await authentication.FetchMetadataAsync(normalized, cancellationToken).ConfigureAwait(false);
        if (!metadata.IsSuccess)
        {
            return Result<AuthServer>.Fail(metadata.Error!);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = store.Load();

            // 网络请求期间可能有另一个添加操作先完成，提交前重新检查。
            var current = document.Servers.FirstOrDefault(server =>
                string.Equals(server.Url, normalized, StringComparison.OrdinalIgnoreCase));
            if (current is not null)
            {
                if (requestedRegisterUrl is not null && requestedRegisterUrl != current.RegisterUrl)
                {
                    current = current with { RegisterUrl = requestedRegisterUrl };
                    Replace(document, current);
                    store.Save(document);
                }

                return Result<AuthServer>.Ok(current);
            }

            var server = new AuthServer
            {
                Id = BuildId(normalized),
                Url = normalized,
                Name = metadata.Value!.ServerName,
                RegisterUrl = requestedRegisterUrl ?? NormalizeRegisterUrl(metadata.Value.RegisterUrl),
                AddedAt = DateTimeOffset.UtcNow,
            };

            document.Servers.Add(server);
            store.Save(document);
            logger.LogInformation("已添加认证服务器 {Name}（{Url}）。", server.Name, server.Url);
            return Result<AuthServer>.Ok(server);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Result<AuthServer>> SaveRegisterUrlAsync(
        AuthServer existing,
        string? requestedRegisterUrl,
        CancellationToken cancellationToken)
    {
        if (requestedRegisterUrl is null ||
            string.Equals(requestedRegisterUrl, existing.RegisterUrl, StringComparison.Ordinal))
        {
            return Result<AuthServer>.Ok(existing);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = store.Load();
            var current = document.Servers.FirstOrDefault(server =>
                string.Equals(server.Id, existing.Id, StringComparison.OrdinalIgnoreCase));
            if (current is null)
            {
                return Result<AuthServer>.Ok(existing);
            }

            current = current with { RegisterUrl = requestedRegisterUrl };
            Replace(document, current);
            store.Save(document);
            return Result<AuthServer>.Ok(current);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Result<AuthServer>> UpdateAsync(
        string id,
        string? url,
        string? registerUrl,
        CancellationToken cancellationToken = default)
    {
        var current = await GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return Result<AuthServer>.Fail("authserver.not_found", $"认证服务器不存在：{id}");
        }

        var normalized = current.Url;
        var name = current.Name;
        var urlChanged = false;

        if (!string.IsNullOrWhiteSpace(url))
        {
            if (!AuthlibAuthenticationService.TryNormalizeUrl(url, out normalized))
            {
                return Result<AuthServer>.Fail(
                    "authserver.invalid_url",
                    AuthlibAuthenticationService.HttpsRequiredMessage);
            }

            urlChanged = !string.Equals(normalized, current.Url, StringComparison.OrdinalIgnoreCase);
            if (urlChanged)
            {
                var servers = await GetServersAsync(cancellationToken).ConfigureAwait(false);
                var clash = servers.FirstOrDefault(server =>
                    !string.Equals(server.Id, current.Id, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(server.Url, normalized, StringComparison.OrdinalIgnoreCase));
                if (clash is not null)
                {
                    return Result<AuthServer>.Fail(
                        "authserver.duplicate",
                        $"该地址已经是另一个认证服务器：{clash.Name}");
                }

                // 先验证新服务器并取回元数据，再提交本地修改；网络请求不占锁。
                var metadata = await authentication
                    .FetchMetadataAsync(normalized, cancellationToken)
                    .ConfigureAwait(false);
                if (!metadata.IsSuccess)
                {
                    return Result<AuthServer>.Fail(metadata.Error!);
                }

                name = metadata.Value!.ServerName;
            }
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = store.Load();
            var server = document.Servers.FirstOrDefault(item =>
                string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
            if (server is null)
            {
                return Result<AuthServer>.Fail("authserver.not_found", $"认证服务器不存在：{id}");
            }

            // 验证期间服务器可能被其它操作修改；避免把已通过验证的旧结果覆盖到新状态上。
            if (!string.Equals(server.Url, current.Url, StringComparison.OrdinalIgnoreCase))
            {
                return Result<AuthServer>.Fail(
                    "authserver.concurrent_update",
                    "认证服务器已被其他操作修改，请刷新后重试。");
            }

            if (urlChanged)
            {
                var clash = document.Servers.FirstOrDefault(item =>
                    !string.Equals(item.Id, server.Id, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(item.Url, normalized, StringComparison.OrdinalIgnoreCase));
                if (clash is not null)
                {
                    return Result<AuthServer>.Fail(
                        "authserver.duplicate",
                        $"该地址已经是另一个认证服务器：{clash.Name}");
                }
            }

            var updated = server with
            {
                Url = normalized,
                Name = name,
                RegisterUrl = NormalizeRegisterUrl(registerUrl),
            };

            Replace(document, updated);
            store.Save(document);
            logger.LogInformation("已更新认证服务器 {Name}（{Url}）。", updated.Name, updated.Url);
            return Result<AuthServer>.Ok(updated);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void Replace(AuthServersDocument document, AuthServer server)
    {
        var index = document.Servers.FindIndex(item =>
            string.Equals(item.Id, server.Id, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            document.Servers[index] = server;
        }
    }

    /// <summary>空字符串表示“不填写”；只接受 http/https 绝对地址。</summary>
    private static string? NormalizeRegisterUrl(string? registerUrl)
    {
        if (string.IsNullOrWhiteSpace(registerUrl))
        {
            return null;
        }

        return AuthlibAuthenticationService.TryNormalizeUrl(registerUrl, out var normalized)
            ? normalized
            : null;
    }

    public async Task<Result> RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = store.Load();
            var server = document.Servers.FirstOrDefault(item =>
                string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
            if (server is null)
            {
                return Result.Fail("authserver.not_found", $"认证服务器不存在：{id}");
            }

            var inUse = accounts.Load().Accounts.Count(account =>
                account.Type == AccountType.ThirdParty &&
                string.Equals(account.AuthServerId, server.Id, StringComparison.Ordinal));
            if (inUse > 0)
            {
                return Result.Fail(
                    "authserver.in_use",
                    $"仍有 {inUse} 个账号使用该认证服务器，请先删除这些账号。");
            }

            document.Servers.Remove(server);
            store.Save(document);
            logger.LogInformation("已删除认证服务器 {Name}（{Url}）。", server.Name, server.Url);
            return Result.Ok();
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static string BuildId(string normalizedUrl)
    {
        var host = new Uri(normalizedUrl).Host.ToLowerInvariant();
        var slug = new string(host
            .Select(character => char.IsLetterOrDigit(character) || character is '.' or '-' ? character : '-')
            .ToArray())
            .Trim('-');
        if (slug.Length == 0)
        {
            slug = "authserver";
        }

        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(normalizedUrl)))[..4].ToLowerInvariant();
        return $"{slug}-{hash}";
    }
}
