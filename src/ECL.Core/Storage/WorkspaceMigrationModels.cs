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
    IReadOnlyList<string> Warnings);

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

    Task<Result<WorkspaceMigrationRecord>> StartAsync(string targetPath, CancellationToken cancellationToken = default);

    Task<Result<WorkspaceMigrationRecord>> ResumeAsync(CancellationToken cancellationToken = default);

    /// <summary>只清理 staging 与迁移记录；源工作区与目标目录不受影响。</summary>
    Task<Result> DiscardAsync(CancellationToken cancellationToken = default);
}
