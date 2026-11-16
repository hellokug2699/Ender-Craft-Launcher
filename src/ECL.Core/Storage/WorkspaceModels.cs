// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Core.Storage;

/// <summary>
/// 工作区类型。ECL 管理的工作区由 ECL 创建并承载实例；外部工作区是用户已有的
/// Minecraft 目录，只登记引用，不复制、不移动、不清理。
/// </summary>
public enum WorkspaceKind
{
    Managed,
    External,
}

/// <summary>工作区路径的可用性状态。</summary>
public enum WorkspaceStatus
{
    /// <summary>尚未验证。</summary>
    Unknown,

    /// <summary>存在、可写，可以承载实例。</summary>
    Ready,

    /// <summary>登记过，但当前找不到目录（盘符变化、移动盘离线等）。</summary>
    Missing,

    /// <summary>目录存在但不可写，或无法创建。</summary>
    NotWritable,

    /// <summary>目录存在但不可读/无枚举权限。</summary>
    NotAccessible,

    /// <summary>网络盘/可移动盘当前不可用。</summary>
    Unavailable,

    /// <summary>路径本身非法（空、相对路径、系统目录、用户数据目录等）。</summary>
    Invalid,
}

/// <summary>
/// 一个已登记的工作区根目录。Id 由规范化路径派生，登记后保持稳定：
/// 重新定位（<c>RelocateRootAsync</c>）不重算 Id，只有显式新增根才生成新 Id。
/// </summary>
public sealed record WorkspaceRoot(
    string Id,
    string Name,
    string Path,
    WorkspaceKind Kind,
    WorkspaceStatus Status = WorkspaceStatus.Unknown,
    DateTimeOffset? LastValidatedAtUtc = null,
    string? Structure = null,
    DateTimeOffset? LastScannedAtUtc = null,
    string? LastScanError = null,
    bool Enabled = true,
    bool AutoScan = true,
    string? CanonicalPath = null,
    string? PathIdentityKey = null,
    DateTimeOffset? LastScanAttemptAtUtc = null,
    string? LastScanErrorCode = null,
    WorkspaceScanPolicy? ScanPolicy = null);

/// <summary>
/// 单个来源的扫描策略（F-02 §4.1）。默认值集中在领域层，UI/CLI 只能覆盖有界范围。
/// 超时以秒为单位存储，读取时统一裁剪到安全区间。
/// </summary>
public sealed record WorkspaceScanPolicy(
    int MaxDepth = 3,
    int MaxEntries = 10_000,
    int PerRootTimeoutSeconds = 30)
{
    public const int MaxDepthCeiling = 16;
    public const int MaxEntriesCeiling = 100_000;
    public const int TimeoutCeilingSeconds = 600;

    public static WorkspaceScanPolicy Default { get; } = new();

    public WorkspaceScanPolicy Clamp() => new(
        Math.Clamp(MaxDepth, 1, MaxDepthCeiling),
        Math.Clamp(MaxEntries, 1, MaxEntriesCeiling),
        Math.Clamp(PerRootTimeoutSeconds, 1, TimeoutCeilingSeconds));
}

/// <summary>
/// <c>config/workspace.json</c> 的持久化模型。单一主工作区 + 已登记根目录列表；
/// 列表结构为后续多根目录留出扩展空间，不允许把“唯一根目录”写死进路径 API。
/// schema v2 新增字段全部可选，读取 v1 时补默认值，首次保存写 v2。
/// </summary>
public sealed record WorkspaceFile
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public string? PrimaryId { get; init; }

    public IReadOnlyList<WorkspaceRoot> Roots { get; init; } = [];

    public DateTimeOffset? OnboardingCompletedAtUtc { get; init; }

    public bool OnboardingSkipped { get; init; }
}

/// <summary>校验模式：新建工作区允许创建目录；已登记工作区必须仍然存在；外部根只读。</summary>
public enum WorkspaceCheckMode
{
    /// <summary>允许创建缺失目录（用户选择新位置时使用）。</summary>
    CreateIfMissing,

    /// <summary>目录必须已存在且可写（校验已登记 Managed 工作区时使用，避免伪造空目录）。</summary>
    RequireExisting,

    /// <summary>
    /// 目录必须已存在且可读，但不要求可写、不写探针、不创建目录。
    /// 外部来源根/扫描只允许使用该模式（ADR 0023 §7、ADR 0046 §3、D1-1）。
    /// </summary>
    ReadOnlyExisting,
}

/// <summary>一次路径校验的结果。可解释、可用于 UI 与 CLI 的直接展示。</summary>
public sealed record WorkspacePathCheck(
    string Path,
    bool IsUsable,
    WorkspaceStatus Status,
    string? ProblemCode,
    string? ProblemMessage,
    long? FreeBytes,
    IReadOnlyList<string> WarningCodes)
{
    /// <summary>解析重解析点后的最终路径（可用时）；用于边界与去重，不强制展示。</summary>
    public string? CanonicalPath { get; init; }

    /// <summary>卷序列号 + 文件 ID（可用时）；用于识别同一物理目录的别名。</summary>
    public string? PathIdentityKey { get; init; }

    public bool HasWarnings => WarningCodes.Count > 0;

    public static WorkspacePathCheck Invalid(string path, string code, string message) =>
        new(path, false, WorkspaceStatus.Invalid, code, message, null, []);
}

/// <summary>推荐的工作区位置。ReasonCode 供 UI 选择解释文案。</summary>
public sealed record WorkspaceSuggestion(string Path, string ReasonCode, bool IsRecommended);

/// <summary>旧版布局检测结果：数据仍在启动器数据目录下时，引导用户确认或迁移。</summary>
public sealed record WorkspaceLegacyInfo(bool Found, string Path, int InstanceCount, int VersionCount);

/// <summary>解除来源登记的方式。默认只解除登记，不删除磁盘文件。</summary>
public enum RootRemovalMode
{
    /// <summary>解除登记；发现目录条目转为未绑定，保留证据与已导入实例记录。</summary>
    Unbind,

    /// <summary>解除登记并移除该来源在发现目录中的条目；仍不删除磁盘文件或实例记录。</summary>
    Forget,
}

/// <summary>一个活动引用（游戏会话/写入任务），用于解绑来源前的一致性检查。</summary>
public sealed record ActiveInstanceReference(string InstanceId, string? SessionId, string Description);

/// <summary>
/// 来源活动探测（F-02 §5.4）。没有探测实现时必须保守阻止解绑，不得声称“无活动”。
/// </summary>
public interface IInstanceSourceActivityProbe
{
    Task<Result<IReadOnlyList<ActiveInstanceReference>>> FindActiveAsync(
        string rootId,
        CancellationToken cancellationToken = default);
}
