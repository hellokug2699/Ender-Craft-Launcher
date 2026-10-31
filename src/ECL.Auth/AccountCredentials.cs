// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Auth;

/// <summary>
/// Microsoft 账户与第三方（Authlib-Injector）账户的秘密，保存在普通账户文档之外，
/// 由凭据存储在磁盘上加密。
/// </summary>
public sealed record AccountCredentials
{
    /// <summary>交给 Minecraft 客户端的 access token（Microsoft 为 Minecraft Services token，第三方为 Yggdrasil token）。</summary>
    public required string MinecraftAccessToken { get; init; }

    public string? MicrosoftRefreshToken { get; init; }

    public DateTimeOffset? MinecraftTokenExpiresAt { get; init; }

    /// <summary>Yggdrasil client token；与 access token 成对用于校验/刷新。</summary>
    public string? AuthlibClientToken { get; init; }
}
