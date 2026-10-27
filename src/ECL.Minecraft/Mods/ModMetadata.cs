// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Minecraft.Mods;

public enum ModPlatform
{
    Unknown,
    Fabric,
    Forge,
    NeoForge,
    LegacyForge,
}

public sealed record ModDependency(
    string ModId,
    bool Mandatory,
    string? VersionRange = null);

public sealed record ModMetadata(
    string ModId,
    string Name,
    string Version,
    string? Description,
    ModPlatform Platform,
    IReadOnlyList<ModDependency> Dependencies,
    IReadOnlyList<string> Breaks);

public sealed record ModMetadataReadResult(ModMetadata? Metadata, string? Error)
{
    public bool IsSuccess => Metadata is not null;
}
