// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Core.Workspace.Session;

/// <summary>
/// 一个可恢复的工作区条目。字段全部可空，因为会话快照会跨版本存活：
/// 旧版本写入的快照可能只记录了实例，没有工具或具体目的地。
/// </summary>
public sealed record WorkspaceSessionEntry(string? ToolId, string? InstanceId, string? Destination);

/// <summary>
/// 工作区会话快照（F-12 最小切片）。它只描述“上次打开了什么”，
/// 不包含内容、授权或游戏进程状态，因此恢复询问不会替用户做任何决定。
/// </summary>
public sealed record WorkspaceSessionSnapshot(
    int SchemaVersion,
    IReadOnlyList<WorkspaceSessionEntry> Open,
    int ActiveIndex,
    string? NavigationDestination,
    DateTimeOffset CapturedAt)
{
    /// <summary>当前写入的 schema 版本。不兼容的旧值不会被猜测性解释。</summary>
    public const int CurrentSchemaVersion = 1;
}

/// <summary>
/// 上一次 <see cref="IWorkspaceSessionStore.Load"/> 的结果。
/// 只有区分“没有快照”“快照损坏”“快照版本不兼容”，启动时才能给出准确询问，
/// 而不是把损坏静默当成“首次启动”。
/// </summary>
public enum WorkspaceSessionLoadStatus
{
    None,
    Available,
    Unreadable,
    Incompatible,
}

/// <summary>会话快照的持久化入口。实现必须保证损坏/不可读时返回 null 而不是抛异常。</summary>
public interface IWorkspaceSessionStore
{
    /// <summary>最近一次 <see cref="Load"/> 的结果，供恢复询问区分“没有”和“不可用”。</summary>
    WorkspaceSessionLoadStatus LastLoadStatus { get; }

    WorkspaceSessionSnapshot? Load();

    void Save(WorkspaceSessionSnapshot snapshot);

    void Clear();
}
