// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Core.Workspace;

/// <summary>
/// 工作区目标类型（迁移规划 §4.3）。B2 引入 Instance；File / World 仍在各自消费者批次引入。
/// </summary>
public enum WorkspaceTargetKind
{
    /// <summary>全局目的地工作区：没有目标资源，天然单例。</summary>
    Destination = 0,

    /// <summary>实例工作区：目标是 ADR 0046 的稳定实例标识。</summary>
    Instance = 1,
}

/// <summary>
/// 强类型工作区目标：<c>(Kind, SchemaVersion, 标识)</c>。
///
/// 它只表达**身份**：恢复、去重与深链接引用。它不是文件授权器，也不是路径安全边界；
/// 路径越界、符号链接/重解析点等校验属于受控资源访问服务（ADR 0018/0022/0042/0045）。
///
/// 目标只能通过 <see cref="WorkspaceTargetSerializer"/> 或
/// <see cref="WorkspaceTargetCompatibility"/> 构造，确保同一会话里不会同时出现
/// 旧写法与规范化后的等价身份。
/// </summary>
public sealed record WorkspaceTarget
{
    /// <summary>目的地目标使用的 schema 版本。</summary>
    public const int DestinationSchemaVersion = 1;

    /// <summary>实例目标使用的 schema 版本。</summary>
    public const int InstanceSchemaVersion = 1;

    private WorkspaceTarget(WorkspaceTargetKind kind, int schemaVersion, string? instanceId)
    {
        Kind = kind;
        SchemaVersion = schemaVersion;
        StableInstanceId = instanceId;
    }

    /// <summary>全局目的地：没有目标资源。</summary>
    public static WorkspaceTarget Destination { get; } =
        new(WorkspaceTargetKind.Destination, DestinationSchemaVersion, instanceId: null);

    public WorkspaceTargetKind Kind { get; }

    /// <summary>身份表示使用的 schema 版本。</summary>
    public int SchemaVersion { get; }

    /// <summary>稳定实例标识（ADR 0046）；目的地为 <c>null</c>。</summary>
    public string? StableInstanceId { get; }

    public bool IsDestination => Kind == WorkspaceTargetKind.Destination;

    public bool IsInstance => Kind == WorkspaceTargetKind.Instance;

    /// <summary>由 serializer 在完成语法校验与规范化后调用。</summary>
    internal static WorkspaceTarget CreateInstance(string normalizedInstanceId) =>
        new(WorkspaceTargetKind.Instance, InstanceSchemaVersion, normalizedInstanceId);
}
