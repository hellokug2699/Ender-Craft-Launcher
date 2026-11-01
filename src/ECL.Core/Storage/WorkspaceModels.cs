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

    /// <summary>路径本身非法（空、相对路径、系统目录、用户数据目录等）。</summary>
    Invalid,
}

/// <summary>一个已登记的工作区根目录。Id 由规范化路径派生，稳定且与显示名称无关。</summary>
public sealed record WorkspaceRoot(
    string Id,
    string Name,
    string Path,
    WorkspaceKind Kind,
    WorkspaceStatus Status = WorkspaceStatus.Unknown,
    DateTimeOffset? LastValidatedAtUtc = null);

/// <summary>
/// <c>config/workspace.json</c> 的持久化模型。单一主工作区 + 已登记根目录列表；
/// 列表结构为后续多根目录留出扩展空间，不允许把“唯一根目录”写死进路径 API。
/// </summary>
public sealed record WorkspaceFile
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public string? PrimaryId { get; init; }

    public IReadOnlyList<WorkspaceRoot> Roots { get; init; } = [];

    public DateTimeOffset? OnboardingCompletedAtUtc { get; init; }

    public bool OnboardingSkipped { get; init; }
}

/// <summary>校验模式：新建工作区允许创建目录；已登记工作区必须仍然存在。</summary>
public enum WorkspaceCheckMode
{
    /// <summary>允许创建缺失目录（用户选择新位置时使用）。</summary>
    CreateIfMissing,

    /// <summary>目录必须已存在（校验已登记工作区时使用，避免伪造空目录）。</summary>
    RequireExisting,
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
    public bool HasWarnings => WarningCodes.Count > 0;

    public static WorkspacePathCheck Invalid(string path, string code, string message) =>
        new(path, false, WorkspaceStatus.Invalid, code, message, null, []);
}

/// <summary>推荐的工作区位置。ReasonCode 供 UI 选择解释文案。</summary>
public sealed record WorkspaceSuggestion(string Path, string ReasonCode, bool IsRecommended);

/// <summary>旧版布局检测结果：数据仍在启动器数据目录下时，引导用户确认或迁移。</summary>
public sealed record WorkspaceLegacyInfo(bool Found, string Path, int InstanceCount, int VersionCount);
