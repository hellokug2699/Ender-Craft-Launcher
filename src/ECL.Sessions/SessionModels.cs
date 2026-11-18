// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Sessions;

/// <summary>会话生命周期（ADR 0015 / 0019）。进程退出与采集收尾是两个不同阶段。</summary>
public enum GameSessionState
{
    Preparing,
    Starting,
    Running,
    GameExitedDraining,
    CaptureCompleted,
    CaptureIncomplete,
    Interrupted,
    RecoveryPending,
    ProcessStateUnknown,
}

/// <summary>证据与目标进程的关联置信度。文件名本身不得提高置信度。</summary>
public enum ArtifactOwnership
{
    Direct,
    StronglyCorrelated,
    Possible,
    SharedOrAmbiguous,
    Unassociated,
}

/// <summary>
/// 进程输出采集方式（F-09 S3/S9）。采集方式决定“这份会话有没有 stdout/stderr 证据”，
/// 因此必须持久化在 manifest 里，而不是由 UI 或日志页推断。
/// </summary>
public static class SessionCaptureModes
{
    /// <summary>尚未记录（旧会话或采集尚未开始）。</summary>
    public const string Unknown = "unknown";

    /// <summary>独立 LogHost 进程重定向并排空 stdout/stderr。</summary>
    public const string LogHost = "loghost";

    /// <summary>ECL 主进程内重定向并排空 stdout/stderr。</summary>
    public const string Redirect = "redirect";

    /// <summary>
    /// 控制台模式（<c>game.showConsole=true</c>）：进程继承控制台，没有重定向，
    /// 因此本次会话不会产生 stdout/stderr 证据。它是显式的采集空洞，不是“采集完整”。
    /// </summary>
    public const string Console = "console";
}

/// <summary>会话警告代码（manifest.Warnings）。UI 按前缀本地化，未知代码原样显示。</summary>
public static class SessionWarnings
{
    public const string CaptureConsole = "session.capture.console";

    public const string CaptureDropped = "session.capture.dropped";

    public const string CaptureFault = "session.capture.fault";
}

public static class SessionArtifactKinds
{
    public const string Stdout = "stdout";
    public const string Stderr = "stderr";
    public const string GameLog = "game_log";
    public const string JvmFatalError = "jvm_fatal_error";
    public const string ClientCrashReport = "client_crash_report";
    public const string ServerCrashReport = "server_crash_report";
    public const string RotatedLog = "rotated_log";
}

public sealed record SessionArtifact
{
    public required string Id { get; init; }

    public required string Kind { get; init; }

    public string? SourcePath { get; init; }

    public string? CapturedPath { get; init; }

    public long Size { get; init; }

    public DateTimeOffset ObservedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public string? Sha256 { get; init; }

    public ArtifactOwnership Ownership { get; init; } = ArtifactOwnership.Possible;

    public string? Warning { get; init; }
}

/// <summary>单次启动的持久化身份。会话不等于实例、PID、latest.log 或崩溃报告。</summary>
public sealed record SessionManifest
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public required string SessionId { get; init; }

    public string? InstanceId { get; init; }

    public string? InstanceName { get; init; }

    public string? GameDirectory { get; init; }

    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public GameSessionState State { get; init; } = GameSessionState.Preparing;

    public int? ProcessId { get; init; }

    public DateTimeOffset? ProcessStartedAtUtc { get; init; }

    public string? ExecutablePath { get; init; }

    public string? WorkingDirectory { get; init; }

    public string? JavaPath { get; init; }

    public string? JavaVersion { get; init; }

    public string? MinecraftVersion { get; init; }

    public string? Loader { get; init; }

    /// <summary><c>direct</c>、<c>shared</c> 或 <c>unavailable</c>。</summary>
    public string LogIsolation { get; init; } = "unavailable";

    public string? LogConfigFingerprint { get; init; }

    public int? ExitCode { get; init; }

    public long LastDurableSequence { get; init; }

    public string? CaptureHost { get; init; }

    /// <summary>采集宿主进程 ID；用于恢复时判断宿主是否仍存活。</summary>
    public int? CaptureHostProcessId { get; init; }

    public string? CaptureFault { get; init; }

    /// <summary>采集方式（<see cref="SessionCaptureModes"/>）；旧 manifest 为 unknown。</summary>
    public string CaptureMode { get; init; } = SessionCaptureModes.Unknown;

    /// <summary>标准输出与标准错误实际写入会话文件的字节数；仅在记录后有效。</summary>
    public long CaptureWrittenBytes { get; init; }

    /// <summary>因队列饱和或写入故障被丢弃的字节数；大于零表示日志不完整。</summary>
    public long CaptureDroppedBytes { get; init; }

    /// <summary>最近一次采集计量时间；null 表示尚未记录。</summary>
    public DateTimeOffset? CaptureAccountedAtUtc { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public IReadOnlyList<SessionArtifact> Artifacts { get; init; } = [];
}

/// <summary>会话目录与 manifest 的组合视图。</summary>
public sealed record GameLogSession(SessionManifest Manifest, string DirectoryPath)
{
    public string SessionId => Manifest.SessionId;

    public string ManifestPath => Path.Combine(DirectoryPath, "manifest.json");

    public string JournalPath => Path.Combine(DirectoryPath, "events.jsonl");

    public string StdoutPath => Path.Combine(DirectoryPath, "game.stdout.log");

    public string StderrPath => Path.Combine(DirectoryPath, "game.stderr.log");

    public string ArtifactsDirectory => Path.Combine(DirectoryPath, "artifacts");
}

/// <summary>
/// 结构化启动阶段事件（F-09 S1）。阶段事实属于会话 journal，不属于 UI 观察窗口：
/// 窗口关闭、界面重启或进程崩溃都不会让已发生的阶段事件消失。
/// </summary>
public sealed record LaunchStageEvent(
    string StepId,
    string Phase,
    int Index,
    int Total,
    string? Status = null,
    string? Message = null,
    long? DurationMs = null)
{
    public const string PhaseStarted = "started";

    public const string PhaseCompleted = "completed";

    public static LaunchStageEvent Started(string stepId, int index, int total) =>
        new(stepId, PhaseStarted, index, total);

    public static LaunchStageEvent Completed(
        string stepId,
        int index,
        int total,
        string status,
        string? message,
        long? durationMs) =>
        new(stepId, PhaseCompleted, index, total, status, message, durationMs);
}

public sealed record SessionCreateRequest
{
    public string? InstanceId { get; init; }

    public string? InstanceName { get; init; }

    public string? GameDirectory { get; init; }

    public string? JavaPath { get; init; }

    public string? JavaVersion { get; init; }

    public string? MinecraftVersion { get; init; }

    public string? Loader { get; init; }

    public string LogIsolation { get; init; } = "unavailable";
}

public sealed record SessionRecoveryReport(
    int Scanned,
    int Interrupted,
    int Reattached,
    int Unknown,
    IReadOnlyList<string> Warnings);

public sealed record SessionExportResult(string OutputPath, long Bytes, IReadOnlyList<string> Missing);
