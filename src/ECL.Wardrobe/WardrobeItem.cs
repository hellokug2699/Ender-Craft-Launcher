// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Wardrobe;

/// <summary>衣柜资源类型。皮肤与披风是两类不同的材质，能力与校验规则也要分开处理。</summary>
public enum WardrobeTextureType
{
    Skin,
    Cape,
}

/// <summary>
/// 皮肤模型。注意：Classic / Slim 本质上是账户档案或启动器配置中的元数据，
/// PNG 图片本身并不携带这一字段。这里识别出的值只是根据 64x64 皮肤布局做的启发式推断。
/// </summary>
public enum SkinModel
{
    Unknown,
    Classic,
    Slim,
}

/// <summary>
/// 本地衣柜中的一项资源。只保存资源引用与非敏感元数据，绝不保存账户令牌。
/// <see cref="RelativePath"/> 相对衣柜根目录，避免把绝对路径写入索引。
/// </summary>
public sealed record WardrobeItem
{
    /// <summary>衣柜内部稳定 ID（与内容哈希分离，重命名/重新导入不会改变引用语义）。</summary>
    public required string Id { get; init; }

    public required WardrobeTextureType Type { get; init; }

    /// <summary>相对衣柜根目录的资源路径，统一使用 <c>/</c> 分隔。</summary>
    public required string RelativePath { get; init; }

    /// <summary>小写十六进制 SHA-256，用于去重与完整性检查。</summary>
    public required string Sha256 { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    public required SkinModel SkinModel { get; init; }

    /// <summary>用户可编辑的显示名称；不参与身份判断。</summary>
    public required string Name { get; init; }

    public bool IsFavorite { get; init; }

    public DateTimeOffset ImportedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>衣柜索引文件（index.json）的序列化模型。</summary>
public sealed class WardrobeIndex
{
    public int SchemaVersion { get; set; } = 1;

    public List<WardrobeItem> Items { get; set; } = [];
}

/// <summary>读取索引的结果。索引损坏时不抛异常，而是返回空索引并给出诊断信息。</summary>
public sealed record WardrobeIndexLoadResult
{
    public required WardrobeIndex Index { get; init; }

    /// <summary>索引无法解析或部分条目无效时为 true；调用方可以提示用户，但仍可继续使用衣柜。</summary>
    public bool RecoveredFromCorruption { get; init; }

    public string? Warning { get; init; }
}

/// <summary>导入结果。<see cref="Created"/> 为 false 表示按 SHA-256 命中了已有资源。</summary>
public sealed record WardrobeImportResult
{
    public required WardrobeItem Item { get; init; }

    public required bool Created { get; init; }
}

/// <summary>
/// 资源应用状态。必须区分“已选择”“已配置”“游戏内已验证”，
/// 不能因为写入了实例配置就宣称皮肤已经在游戏中显示。
/// </summary>
public enum AppearanceApplicationState
{
    /// <summary>用户已在衣柜中选中该资源，尚未写入任何实例或账户配置。</summary>
    Selected,

    /// <summary>已写入实例的本地外观配置；实际显示仍取决于客户端/服务端/模组。</summary>
    Configured,

    /// <summary>已经通过实际启动游戏或服务端反馈确认显示生效。</summary>
    VerifiedInGame,
}

/// <summary>实例或账户对衣柜资源的一次应用记录。账户全局外观与实例本地外观分开记录。</summary>
public sealed record AppearanceApplication
{
    public required string ItemId { get; init; }

    public required AppearanceApplicationState State { get; init; }

    /// <summary>应用目标账户；为 null 表示这是实例本地配置而不是账户全局外观。</summary>
    public string? AccountId { get; init; }

    /// <summary>应用目标实例；为 null 表示这是账户级选择。</summary>
    public string? InstanceId { get; init; }

    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}
