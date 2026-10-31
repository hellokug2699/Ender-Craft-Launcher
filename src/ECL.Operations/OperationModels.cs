// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Operations;

/// <summary>长时间任务的类型。用于 UI 分组与展示，不影响执行。</summary>
public enum OperationKind
{
    Generic,
    Download,
    Install,
    Import,
    Export,
    Backup,
    Restore,
    Launch,
    Verification,
    Java,
}

/// <summary>
/// 任务生命周期。暂停不是状态机的一部分：它描述运行控制，不描述最终结果。
/// </summary>
public enum OperationState
{
    Pending,
    Running,
    Retrying,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>失败归因。UI 根据它告诉用户“是谁的问题”，不需要解析异常文本。</summary>
public enum OperationErrorKind
{
    None,
    Network,
    Upstream,
    Authentication,
    FileIntegrity,
    LocalIO,
    Configuration,
    EclInternal,
    Cancelled,
}

public sealed record OperationError(
    OperationErrorKind Kind,
    string Code,
    string Message,
    string? Detail = null)
{
    public override string ToString() =>
        string.IsNullOrWhiteSpace(Detail) ? Message : $"{Message} ({Detail})";
}

/// <summary>创建一个 Operation 时提供的不可变信息。</summary>
public sealed record OperationStart
{
    public required OperationKind Kind { get; init; }

    /// <summary>面向用户的短标题（通常是文件名、实例名或任务名）。</summary>
    public required string Title { get; init; }

    /// <summary>稳定的阶段标识（例如 download、verify），UI 按它本地化。</summary>
    public string? StageId { get; init; }

    /// <summary>已本地化的阶段文本；为空时由 UI 按 StageId 翻译。</summary>
    public string? StageText { get; init; }

    /// <summary>来源标识，例如 Mojang、Modrinth、CurseForge 或主机名。</summary>
    public string? Source { get; init; }

    public long? Total { get; init; }

    public long? TotalBytes { get; init; }

    public bool CanCancel { get; init; } = true;

    public bool CanRetry { get; init; }
}

/// <summary>一次进度更新。未提供的字段保持当前值。</summary>
public sealed record OperationProgressUpdate
{
    public string? StageId { get; init; }

    public string? StageText { get; init; }

    public string? CurrentItem { get; init; }

    public long? Completed { get; init; }

    public long? Total { get; init; }

    public long? BytesDownloaded { get; init; }

    public long? TotalBytes { get; init; }

    public double? BytesPerSecond { get; init; }

    public bool? IsPaused { get; init; }
}

/// <summary>
/// 面向 UI / CLI 的只读任务快照。事件流在底层，消费者只需要最新快照。
/// </summary>
public sealed record OperationSnapshot
{
    public required Guid Id { get; init; }

    public required OperationKind Kind { get; init; }

    public required string Title { get; init; }

    public required OperationState State { get; init; }

    public string? StageId { get; init; }

    public string? StageText { get; init; }

    public string? CurrentItem { get; init; }

    /// <summary>已完成的项目数（文件、步骤等）。</summary>
    public long Completed { get; init; }

    public long? Total { get; init; }

    public long BytesDownloaded { get; init; }

    public long? TotalBytes { get; init; }

    public double? BytesPerSecond { get; init; }

    public string? Source { get; init; }

    public OperationError? Error { get; init; }

    public int RetryAttempt { get; init; }

    public int MaxRetryAttempts { get; init; }

    public string? RetryReason { get; init; }

    public bool CanCancel { get; init; }

    public bool CanRetry { get; init; }

    public bool IsPaused { get; init; }

    /// <summary>运行中但长时间没有进度。停滞不是失败，也不应触发自动取消。</summary>
    public bool IsStalled { get; init; }

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public bool IsTerminal => State is OperationState.Completed or OperationState.Failed or OperationState.Cancelled;

    /// <summary>
    /// 字节进度 0..1；总大小未知时为 null（indeterminate）。
    /// 不要用文件数量冒充字节进度，数量进度见 <see cref="ItemProgress"/>。
    /// </summary>
    public double? Progress =>
        TotalBytes is > 0
            ? Math.Clamp((double)BytesDownloaded / TotalBytes.Value, 0, 1)
            : null;

    /// <summary>项目数量进度 0..1（文件、步骤等）；总数未知时为 null。</summary>
    public double? ItemProgress =>
        Total is > 0 ? Math.Clamp((double)Completed / Total.Value, 0, 1) : null;

    /// <summary>剩余时间；速度或总大小未知时为 null。</summary>
    public TimeSpan? Eta
    {
        get
        {
            if (IsTerminal ||
                BytesPerSecond is not > 0 ||
                TotalBytes is not > 0 ||
                TotalBytes <= BytesDownloaded)
            {
                return null;
            }

            return TimeSpan.FromSeconds((TotalBytes.Value - BytesDownloaded) / BytesPerSecond.Value);
        }
    }
}
