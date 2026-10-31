// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Auth;

/// <summary>
/// Microsoft account secrets kept outside the normal account document.
/// The values are transient in memory and are encrypted at rest by the credential store.
/// </summary>
public sealed record AccountCredentials
{
    public required string MinecraftAccessToken { get; init; }

    public string? MicrosoftRefreshToken { get; init; }

    public DateTimeOffset? MinecraftTokenExpiresAt { get; init; }
}
