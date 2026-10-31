// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json.Serialization;

namespace Ecl.Auth;

public enum AccountType
{
    Offline,
    Microsoft,
    ThirdParty,
}

public sealed record Account
{
    public required string Id { get; init; }

    public required AccountType Type { get; init; }

    public required string Name { get; init; }

    public required string Uuid { get; init; }

    /// <summary>Transient Minecraft access token. Never serialize this into accounts.json.</summary>
    [JsonIgnore]
    public string? AccessToken { get; init; }

    /// <summary>Transient Microsoft refresh token. Never serialize this into accounts.json.</summary>
    [JsonIgnore]
    public string? RefreshToken { get; init; }

    /// <summary>Transient Minecraft access token expiry. Never serialize this into accounts.json.</summary>
    [JsonIgnore]
    public DateTimeOffset? MinecraftTokenExpiresAt { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class AccountsDocument
{
    public int SchemaVersion { get; set; } = 1;

    public string? DefaultAccountId { get; set; }

    public List<Account> Accounts { get; set; } = [];
}
