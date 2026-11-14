// SPDX-License-Identifier: GPL-3.0-or-later

using CommunityToolkit.Mvvm.ComponentModel;
using Ecl.Core.Localization;
using Ecl.Sessions;

namespace Ecl.App.ViewModels.LogSessions;

public enum SessionStateFilter
{
    All,
    Running,
    Exited,
    Interrupted,
    Unknown,
}

/// <summary>
/// 左侧会话条目。身份只来自 SessionId；实例名、PID、版本与时间都是 manifest 快照，
/// 不会因为实例改名或最新日志文件变化而改写归属。
/// </summary>
public sealed partial class LogSessionRowViewModel : ObservableObject
{
    private readonly ILocalizer _localizer;

    public LogSessionRowViewModel(GameLogSession session, ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(session);
        _localizer = localizer;
        Id = session.SessionId;
        DirectoryPath = session.DirectoryPath;
        Manifest = session.Manifest;
        Refresh(session.Manifest, session.DirectoryPath);
    }

    public string Id { get; }

    public string DirectoryPath { get; private set; }

    public SessionManifest Manifest { get; private set; }

    /// <summary>当前 manifest 与受控会话目录的组合；读取时重新构造，避免缓存旧状态。</summary>
    public GameLogSession Session => new(Manifest, DirectoryPath);

    public GameSessionState State => Manifest.State;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _instanceName = string.Empty;

    [ObservableProperty]
    private string _meta = string.Empty;

    [ObservableProperty]
    private string _startedText = string.Empty;

    [ObservableProperty]
    private string _stateText = string.Empty;

    [ObservableProperty]
    private string _pidText = string.Empty;

    [ObservableProperty]
    private string _groupTitle = string.Empty;

    /// <summary>分组顺序：运行中/未知 → 最近 → 更早；列表按它稳定排序。</summary>
    [ObservableProperty]
    private int _groupOrder;

    [ObservableProperty]
    private string _evidenceHint = string.Empty;

    [ObservableProperty]
    private bool _hasEvidenceHint;

    [ObservableProperty]
    private bool _isActive;

    public string SearchText { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc => Manifest.CreatedAtUtc;

    public void Refresh(SessionManifest manifest, string directoryPath)
    {
        Manifest = manifest;
        DirectoryPath = directoryPath;

        InstanceName = string.IsNullOrWhiteSpace(manifest.InstanceName)
            ? _localizer.Get("logs.unknownInstance")
            : manifest.InstanceName;
        var version = string.IsNullOrWhiteSpace(manifest.MinecraftVersion)
            ? _localizer.Get("logs.unknownVersion")
            : manifest.MinecraftVersion;
        var loader = string.IsNullOrWhiteSpace(manifest.Loader)
            ? _localizer.Get("logs.unknownLoader")
            : manifest.Loader;
        Title = $"{InstanceName} · {version} · {loader}";
        Meta = $"{manifest.CreatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {manifest.SessionId}";
        StartedText = manifest.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        StateText = DescribeState(manifest, _localizer);
        PidText = manifest.ProcessId is { } pid
            ? _localizer.Get("logs.pid", pid)
            : _localizer.Get("logs.pidUnknown");
        GroupTitle = DescribeGroup(manifest);
        GroupOrder = IsActiveState(manifest.State) ||
                     manifest.State is GameSessionState.RecoveryPending or GameSessionState.ProcessStateUnknown
            ? 0
            : string.Equals(GroupTitle, _localizer.Get("logs.group.recent"), StringComparison.Ordinal)
                ? 1
                : 2;
        IsActive = IsActiveState(manifest.State);

        var hasCrashEvidence = manifest.Artifacts.Any(artifact =>
            artifact.Kind is SessionArtifactKinds.ClientCrashReport
                or SessionArtifactKinds.ServerCrashReport
                or SessionArtifactKinds.JvmFatalError);
        var abnormalExit = manifest.ExitCode is { } exitCode && exitCode != 0;
        HasEvidenceHint = hasCrashEvidence || abnormalExit || manifest.CaptureFault is not null;
        EvidenceHint = hasCrashEvidence
            ? _localizer.Get("logs.hint.crashEvidence")
            : abnormalExit
                ? _localizer.Get("logs.hint.nonZeroExit", manifest.ExitCode!.Value)
                : manifest.CaptureFault is not null
                    ? _localizer.Get("logs.hint.captureFault")
                    : string.Empty;

        SearchText = string.Join(
            ' ',
            manifest.SessionId,
            InstanceName,
            manifest.MinecraftVersion ?? string.Empty,
            manifest.Loader ?? string.Empty,
            manifest.ProcessId?.ToString() ?? string.Empty,
            manifest.InstanceId ?? string.Empty);
    }

    public bool MatchesState(SessionStateFilter filter) =>
        filter switch
        {
            SessionStateFilter.Running => Manifest.State is
                GameSessionState.Preparing or
                GameSessionState.Starting or
                GameSessionState.Running or
                GameSessionState.GameExitedDraining,
            SessionStateFilter.Exited => Manifest.State is
                GameSessionState.CaptureCompleted or
                GameSessionState.CaptureIncomplete,
            SessionStateFilter.Interrupted => Manifest.State == GameSessionState.Interrupted,
            SessionStateFilter.Unknown => Manifest.State is
                GameSessionState.RecoveryPending or
                GameSessionState.ProcessStateUnknown,
            _ => true,
        };

    private string DescribeGroup(SessionManifest manifest)
    {
        if (IsActiveState(manifest.State) ||
            manifest.State is GameSessionState.RecoveryPending or GameSessionState.ProcessStateUnknown)
        {
            return _localizer.Get("logs.group.running");
        }

        var age = DateTimeOffset.UtcNow - manifest.CreatedAtUtc;
        return age <= TimeSpan.FromDays(7)
            ? _localizer.Get("logs.group.recent")
            : _localizer.Get("logs.group.earlier");
    }

    private static bool IsActiveState(GameSessionState state) =>
        state is GameSessionState.Preparing
            or GameSessionState.Starting
            or GameSessionState.Running
            or GameSessionState.GameExitedDraining;

    private static string DescribeState(SessionManifest manifest, ILocalizer localizer)
    {
        var state = manifest.State;
        if (state == GameSessionState.CaptureCompleted)
        {
            return manifest.ExitCode is { } exitCode && exitCode != 0
                ? localizer.Get("logs.state.exitedNonZero", exitCode)
                : localizer.Get("logs.state.exited");
        }

        var key = state switch
        {
            GameSessionState.Preparing or GameSessionState.Starting => "logs.state.starting",
            GameSessionState.Running => "logs.state.running",
            GameSessionState.GameExitedDraining => "logs.state.draining",
            GameSessionState.CaptureIncomplete => "logs.state.incomplete",
            GameSessionState.Interrupted => "logs.state.interrupted",
            GameSessionState.RecoveryPending => "logs.state.recoveryPending",
            GameSessionState.ProcessStateUnknown => "logs.state.unknown",
            _ => "logs.state.unknown",
        };

        return localizer.Get(key);
    }
}
