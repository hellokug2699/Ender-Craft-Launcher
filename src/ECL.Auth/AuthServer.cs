// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json.Serialization;

namespace Ecl.Auth;

/// <summary>
/// 第三方认证服务器（Authlib-Injector / Yggdrasil）。它是独立的一等对象：
/// 多个账号可以指向同一台服务器，同一用户名在不同服务器上代表不同账号。
/// </summary>
public sealed record AuthServer
{
    /// <summary>稳定且可读的本地标识，由服务器地址推导。</summary>
    public required string Id { get; init; }

    /// <summary>认证服务器 API 根地址（元数据端点），统一去掉结尾斜杠。</summary>
    public required string Url { get; init; }

    /// <summary>展示名，优先取元数据里的 <c>meta.serverName</c>。</summary>
    public required string Name { get; init; }

    /// <summary>用户填写或元数据提供的注册页面地址（可选）。</summary>
    public string? RegisterUrl { get; init; }

    public DateTimeOffset AddedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>认证服务器元数据（尽量保持原样，便于诊断与后续扩展）。</summary>
public sealed record AuthServerMetadata
{
    public required string Url { get; init; }

    public required string ServerName { get; init; }

    public string? ImplementationName { get; init; }

    public string? ImplementationVersion { get; init; }

    /// <summary>元数据里的注册链接；用户未填写时作为默认值。</summary>
    public string? RegisterUrl { get; init; }

    [JsonIgnore]
    public IReadOnlyList<string> SkinDomains { get; init; } = [];
}

public sealed class AuthServersDocument
{
    public int SchemaVersion { get; set; } = 1;

    public List<AuthServer> Servers { get; set; } = [];
}

public interface IAuthServerStore
{
    AuthServersDocument Load();

    void Save(AuthServersDocument document);
}
