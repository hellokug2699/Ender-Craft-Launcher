// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Core.Storage;

/// <summary>工作区迁移状态机（ADR 0018 §5）。</summary>
public enum WorkspaceMigrationState
{
    PendingValidation,
    Validating,
    ReadyToCopy,
    Copying,
    Verifying,
    Switching,
    Completed,
    Interrupted,
    Failed,
    RollbackRequired,
    Discarded,
}

/// <summary>
/// 迁移计划中的一条阻塞原因。<see cref="Code"/> 是稳定标识，供 UI 本地化与测试断言；
/// <see cref="Message"/> 保留领域原始说明（CLI、日志与兼容展示使用）。判据本身仍只在领域服务里。
/// </summary>
public sealed record WorkspaceMigrationProblem(string Code, string Message)
{
    /// <summary>需要由 UI 填入本地化文案的参数（例如所需/可用空间），不含路径。</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];
}

/// <summary>迁移前的可解释计划：源/目标、容量、实例数与阻塞原因。</summary>
public sealed record WorkspaceMigrationPlan(
    string SourcePath,
    string TargetPath,
    long TotalBytes,
    int TotalFiles,
    int InstanceCount,
    long? FreeBytes,
    bool CanProceed,
    IReadOnlyList<string> Problems,
    IReadOnlyList<string> Warnings)
{
    /// <summary>与 <see cref="Problems"/> 逐项对应的结构化原因；顺序与数量保持一致。</summary>
    public IReadOnlyList<WorkspaceMigrationProblem> ProblemDetails { get; init; } = [];
}

/// <summary>持久化的迁移记录，用于中断后恢复。绝不记录密钥或账户内容。</summary>
public sealed record WorkspaceMigrationRecord
{
    public required string Id { get; init; }

    public required string SourcePath { get; init; }

    public required string TargetPath { get; init; }

    public required string StagingPath { get; init; }

    public WorkspaceMigrationState State { get; init; } = WorkspaceMigrationState.PendingValidation;

    public DateTimeOffset StartedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public long TotalBytes { get; init; }

    public int TotalFiles { get; init; }

    public int CopiedFiles { get; init; }

    public string? ErrorCode { get; init; }

    public string? ErrorMessage { get; init; }
}

public interface IWorkspaceMigrationService
{
    WorkspaceMigrationRecord? Current { get; }

    Task<Result<WorkspaceMigrationPlan>> PlanAsync(string targetPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// 复制→校验→切换。源目录只读、不删除；<paramref name="progress"/> 收到真实的阶段与文件计数
    /// （复制/校验/切换），UI 不得自行推算阶段。
    /// </summary>
    Task<Result<WorkspaceMigrationRecord>> StartAsync(
        string targetPath,
        IProgress<WorkspaceMigrationRecord>? progress = null,
        CancellationToken cancellationToken = default);

    Task<Result<WorkspaceMigrationRecord>> ResumeAsync(
        IProgress<WorkspaceMigrationRecord>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 只重试“文件已复制但切换工作区配置失败”的最后一步（<see cref="WorkspaceMigrationState.RollbackRequired"/>）。
    /// 不重新复制文件；其它状态由 <see cref="ResumeAsync"/> 处理。
    /// </summary>
    Task<Result<WorkspaceMigrationRecord>> CompleteSwitchAsync(CancellationToken cancellationToken = default);

    /// <summary>只清理 staging 与迁移记录；源工作区与目标目录不受影响。</summary>
    Task<Result> DiscardAsync(CancellationToken cancellationToken = default);
}
