// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Minecraft.Instances;

public sealed record GameInstance
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string GameVersion { get; init; }

    /// <summary>实际使用的版本目录名。整合包/加载器会生成带后缀的版本 ID。</summary>
    public required string VersionId { get; init; }

    public string Loader { get; init; } = "vanilla";

    public string? LoaderVersion { get; init; }

    /// <summary>Fabric 实例必装 Fabric API 的版本号。</summary>
    public string? FabricApiVersion { get; init; }

    /// <summary>用户显式选择的 OptiFine 版本键（type:patch）；为空表示不安装。Forge 与 Cleanroom 可用。</summary>
    public string? OptiFineVersion { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastPlayedAt { get; init; }

    public string? Notes { get; init; }
}
