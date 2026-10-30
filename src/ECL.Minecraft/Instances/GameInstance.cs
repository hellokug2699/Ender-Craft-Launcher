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

    /// <summary>
    /// 版本隔离。ECL 始终为每个实例使用独立的 <c>minecraft</c> 游戏目录，
    /// 该值为 <see langword="true"/> 且不提供关闭入口（UI 中不显示该选项）。
    /// </summary>
    public bool VersionIsolation { get; init; } = InstanceIsolation.Enabled;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastPlayedAt { get; init; }

    public string? Notes { get; init; }
}

/// <summary>实例隔离策略。版本隔离是启动器行为，不暴露为用户可关闭的选项。</summary>
public static class InstanceIsolation
{
    public const bool Enabled = true;
}
