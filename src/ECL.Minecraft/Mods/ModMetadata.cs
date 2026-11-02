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
    IReadOnlyList<string> Breaks,
    IReadOnlyList<string>? Provides = null)
{
    /// <summary>
    /// 该 Mod 额外提供的虚拟 Mod ID。Fabric API 等聚合 Mod 会通过 provides 提供
    /// fabric-rendering-fluids-v1 等模块，依赖检查不能把这些模块判为缺失。
    /// </summary>
    public IReadOnlyList<string> ProvidedIds => Provides ?? [];
}

public sealed record ModMetadataReadResult(ModMetadata? Metadata, string? Error)
{
    public bool IsSuccess => Metadata is not null;
}
