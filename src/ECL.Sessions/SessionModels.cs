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
