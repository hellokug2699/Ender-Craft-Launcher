// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Core.Workspace;

/// <summary>
/// 工作区目标类型。B0 只登记当前消费者真正需要的类型：
/// 全局目的地（无目标资源）与旧版裸资源引用（现存实例 ID）。
/// 迁移规划 §4.3 要求 Instance / File / World 类型在各自消费者批次引入，
/// 因此这里不预先定义 <c>instance:</c> / <c>file:</c> / <c>world:</c> 的版本化身份。
/// </summary>
public enum WorkspaceTargetKind
{
    /// <summary>全局目的地工作区：没有目标资源，天然单例。</summary>
    Destination = 0,

    /// <summary>
    /// 旧版裸目标资源（当前唯一用途是实例 ID）。它不带类型与版本前缀，
    /// 只用于在引入 <c>instance:v1:</c> 之前保持既有工作区身份可用。
    /// </summary>
    Legacy = 1,
}

/// <summary>
/// 强类型工作区目标：<c>(Kind, SchemaVersion, Resource)</c>。
///
/// 它只表达**身份**：恢复、去重与深链接引用。它不是文件授权器，也不是路径安全边界；
/// 路径越界、符号链接/重解析点等校验属于受控资源访问服务（ADR 0018/0022/0042/0045）。
///
/// 实例只能通过 <see cref="WorkspaceTargetSerializer"/> 或
/// <see cref="WorkspaceTargetCompatibility"/> 构造，确保同一会话里不会同时出现
/// 旧、新两种等价身份。
/// </summary>
public sealed record WorkspaceTarget
{
    /// <summary>目的地目标使用的 schema 版本。</summary>
    public const int DestinationSchemaVersion = 1;

    /// <summary>不带版本前缀的旧目标资源。</summary>
    public const int UnversionedSchemaVersion = 0;

    private WorkspaceTarget(WorkspaceTargetKind kind, int schemaVersion, string? resource)
    {
        Kind = kind;
        SchemaVersion = schemaVersion;
        Resource = resource;
    }

    /// <summary>全局目的地：<see cref="Resource"/> 为 <c>null</c>。</summary>
    public static WorkspaceTarget Destination { get; } =
        new(WorkspaceTargetKind.Destination, DestinationSchemaVersion, resource: null);

    public WorkspaceTargetKind Kind { get; }

    /// <summary>身份表示使用的 schema 版本；旧裸资源为 <see cref="UnversionedSchemaVersion"/>。</summary>
    public int SchemaVersion { get; }

    /// <summary>规范化后的目标资源；目的地为 <c>null</c>。</summary>
    public string? Resource { get; }

    public bool IsDestination => Kind == WorkspaceTargetKind.Destination;

    public bool IsLegacyResource => Kind == WorkspaceTargetKind.Legacy;

    /// <summary>
    /// 旧裸资源的规范化载荷。B0 不提供 <c>StableInstanceId</c>，因为实例身份类型在 B2 才引入。
    /// </summary>
    public string? LegacyResource => Kind == WorkspaceTargetKind.Legacy ? Resource : null;

    /// <summary>由 serializer 在完成语法校验与规范化后调用。</summary>
    internal static WorkspaceTarget CreateLegacy(string normalizedResource) =>
        new(WorkspaceTargetKind.Legacy, UnversionedSchemaVersion, normalizedResource);
}
