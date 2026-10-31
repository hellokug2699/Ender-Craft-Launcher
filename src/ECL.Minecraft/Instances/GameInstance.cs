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
    /// 版本隔离。ECL 自建实例始终使用独立的 <c>minecraft</c> 游戏目录；
    /// 导入的既有安装沿用其原始目录，此时该值反映原目录是否按版本隔离。
    /// </summary>
    public bool VersionIsolation { get; init; } = InstanceIsolation.Enabled;

    /// <summary>
    /// 外部游戏目录（绝对路径）。为空表示使用 ECL 自建的 <c>instances/&lt;id&gt;/minecraft</c>。
    /// 导入既有安装时指向原目录，ECL 不复制、不改写其中的文件。
    /// </summary>
    public string? GameDirectory { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastPlayedAt { get; init; }

    public string? Notes { get; init; }
}

/// <summary>实例隔离策略。版本隔离是启动器行为，不暴露为用户可关闭的选项。</summary>
public static class InstanceIsolation
{
    public const bool Enabled = true;
}
