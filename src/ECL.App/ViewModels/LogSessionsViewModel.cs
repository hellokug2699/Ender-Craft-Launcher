// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.App.Services;
using Ecl.Core.Diagnostics;
using Ecl.Core.Localization;
using Ecl.Sessions;

namespace Ecl.App.ViewModels;

/// <summary>
/// 游戏日志会话页：只展示会话与日志，不负责进程生命周期，也不自行扫描实例目录。
/// 视图从文件增量读取，关闭或切换页面不会停止采集，也不会删除会话证据。
/// </summary>
public sealed partial class LogSessionsViewModel : ObservableObject, IDisposable
{
    private const int MaxVisibleLines = 2000;
    private int _maxLines = MaxVisibleLines;
    private const int InitialTailBytes = 64 * 1024;

    private readonly IGameSessionService _sessions;
    private readonly ISessionExportService _exporter;
    private readonly ILocalizer _localizer;
    private readonly IMemoryPressureMonitor _pressure;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<string, StreamReader> _readers = new(StringComparer.Ordinal);
    private bool _disposed;

    public ObservableCollection<SessionRow> Sessions { get; } = [];

    public ObservableCollection<string> Lines { get; } = [];

    [ObservableProperty]
    private SessionRow? _selectedSession;

    [ObservableProperty]
    private bool _follow = true;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private string _sessionDetail = string.Empty;

    [ObservableProperty]
    private bool _showWarnings;

    private string? _targetSessionId;
    private bool _suppressTargetReset;

    /// <summary>
    /// 由 Home 或实例工作区带入的目标会话不可用时的说明（logs.md §7.1）。
    /// 有说明时不自动改看其它会话；用户手动选择会话后清空。
    /// </summary>
    [ObservableProperty]
    private string _targetUnavailableMessage = string.Empty;

    public bool HasTargetUnavailable => !string.IsNullOrWhiteSpace(TargetUnavailableMessage);

    public LogSessionsViewModel(
        IGameSessionService sessions,
        ISessionExportService exporter,
        ILocalizer localizer,
        IMemoryPressureMonitor pressure)
    {
        _sessions = sessions;
        _exporter = exporter;
        _localizer = localizer;
        _pressure = pressure;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => OnTick();
        _pressure.LevelChanged += OnPressureChanged;
    }

    public void Activate()
    {
        RefreshSessions();
        _timer.Start();
    }

    /// <summary>
    /// 携带会话身份打开日志页（home.md §3.3、logs.md §7.1）：定位指定 SessionId；
    /// 会话不存在时保留说明并保持“没有选中会话”，不静默改看最新会话。
    /// </summary>
    public void FocusSession(string sessionId)
    {
        var target = string.IsNullOrWhiteSpace(sessionId) ? null : sessionId;
        _targetSessionId = target;
        TargetUnavailableMessage = string.Empty;
        OnPropertyChanged(nameof(HasTargetUnavailable));

        // 刷新会临时选中第一行；这不是用户主动切换，不能因此丢掉目标身份。
        _suppressTargetReset = true;
        try
        {
            Activate();
        }
        finally
        {
            _suppressTargetReset = false;
        }

        if (target is null)
        {
            return;
        }

        var row = Sessions.FirstOrDefault(session =>
            string.Equals(session.Id, target, StringComparison.Ordinal));
        if (row is not null)
        {
            SelectedSession = row;
            return;
        }

        SelectedSession = null;
        _targetSessionId = target;
        TargetUnavailableMessage = _localizer.Get("logs.targetUnavailable", target);
        OnPropertyChanged(nameof(HasTargetUnavailable));
    }

    public void Deactivate() => _timer.Stop();

    [RelayCommand]
    private void RefreshSessions()
    {
        var selectedId = SelectedSession?.Id;
        var previous = Sessions.Select(row => row.Id).ToArray();
        var sessions = _sessions.List();

        Sessions.Clear();
        foreach (var session in sessions)
        {
            Sessions.Add(new SessionRow(session, _localizer));
        }

        ShowWarnings = sessions.Any(session => session.Manifest.Warnings.Count > 0);
        SelectedSession = Sessions.FirstOrDefault(row => row.Id == selectedId) ?? Sessions.FirstOrDefault();

        if (previous.Length != Sessions.Count)
        {
            StatusMessage = _localizer.Get("logs.refreshed", Sessions.Count);
        }
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (SelectedSession is not { } row)
        {
            return;
        }

        var result = await _exporter.ExportAsync(row.Id).ConfigureAwait(true);
        StatusMessage = result.IsSuccess
            ? _localizer.Get("logs.exported", result.Value!.OutputPath)
            : _localizer.Get("logs.exportFailed", result.Error?.Message ?? string.Empty);
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (SelectedSession is { } row && Directory.Exists(row.DirectoryPath))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(row.DirectoryPath)
            {
                UseShellExecute = true,
            });
        }
    }

    [RelayCommand]
    private void ClearView() => Lines.Clear();

    partial void OnSelectedSessionChanged(SessionRow? value)
    {
        ResetReaders();
        Lines.Clear();
        if (value is null)
        {
            SessionDetail = string.Empty;
            return;
        }

        // 用户手动切到别的会话后，不再保留“目标会话不可用”的说明。
        if (!_suppressTargetReset
            && _targetSessionId is { Length: > 0 } target
            && !string.Equals(value.Id, target, StringComparison.Ordinal))
        {
            _targetSessionId = null;
            TargetUnavailableMessage = string.Empty;
            OnPropertyChanged(nameof(HasTargetUnavailable));
        }

        SessionDetail = value.Detail;
        LoadInitialTail(value);
    }

    private void OnPressureChanged(object? sender, MemoryPressureLevel level)
    {
        switch (level)
        {
            case MemoryPressureLevel.Critical:
                _maxLines = 400;
                _timer.Interval = TimeSpan.FromSeconds(4);
                TrimLines(_maxLines);
                StatusMessage = _localizer.Get("logs.pressure.critical");
                break;
            case MemoryPressureLevel.Elevated:
                _maxLines = 800;
                _timer.Interval = TimeSpan.FromSeconds(2);
                TrimLines(_maxLines);
                StatusMessage = _localizer.Get("logs.pressure.elevated");
                break;
            case MemoryPressureLevel.Recovery:
                _maxLines = 1200;
                _timer.Interval = TimeSpan.FromSeconds(2);
                TrimLines(_maxLines);
                StatusMessage = _localizer.Get("logs.pressure.recovery");
                break;
            default:
                _maxLines = MaxVisibleLines;
                _timer.Interval = TimeSpan.FromSeconds(1);
                StatusMessage = string.Empty;
                break;
        }
    }

    private void TrimLines(int maxLines)
    {
        while (Lines.Count > maxLines)
        {
            Lines.RemoveAt(0);
        }
    }

    private void OnTick()
    {
        if (_disposed || SelectedSession is not { } row)
        {
            return;
        }

        try
        {
            var manifest = _sessions.Get(row.Id)?.Manifest;
            if (manifest is not null)
            {
                row.Update(manifest, _localizer);
                SessionDetail = row.Detail;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 读取状态失败不应打断日志查看。
        }

        if (Follow)
        {
            ReadTail(row);
        }
    }

    private void LoadInitialTail(SessionRow row)
    {
        AppendLines(row.StdoutPath, initial: true);
        AppendLines(row.StderrPath, initial: true);
    }

    private void ReadTail(SessionRow row)
    {
        AppendLines(row.StdoutPath, initial: false);
        AppendLines(row.StderrPath, initial: false);
    }

    private void AppendLines(string path, bool initial)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            if (!_readers.TryGetValue(path, out var reader))
            {
                var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (initial && stream.Length > InitialTailBytes)
                {
                    stream.Seek(-InitialTailBytes, SeekOrigin.End);
                }

                reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                _readers[path] = reader;
            }

            while (reader.ReadLine() is { } line)
            {
                Lines.Add(line);
            }

            while (Lines.Count > _maxLines)
            {
                Lines.RemoveAt(0);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ResetReader(path);
        }
    }

    private void ResetReader(string path)
    {
        if (_readers.Remove(path, out var reader))
        {
            reader.Dispose();
        }
    }

    private void ResetReaders()
    {
        foreach (var reader in _readers.Values)
        {
            reader.Dispose();
        }

        _readers.Clear();
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Stop();
        ResetReaders();
    }
}

/// <summary>会话列表行。状态与证据数量会随采集推进更新。</summary>
public sealed partial class SessionRow : ObservableObject
{
    private string _detail = string.Empty;

    public SessionRow(GameLogSession session, ILocalizer localizer)
    {
        Id = session.SessionId;
        DirectoryPath = session.DirectoryPath;
        StdoutPath = session.StdoutPath;
        StderrPath = session.StderrPath;
        Update(session.Manifest, localizer);
    }

    public string Id { get; }

    public string DirectoryPath { get; }

    public string StdoutPath { get; }

    public string StderrPath { get; }

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _subtitle = string.Empty;

    [ObservableProperty]
    private string _state = string.Empty;

    public string Detail => _detail;

    public void Update(SessionManifest manifest, ILocalizer localizer)
    {
        Title = $"{manifest.InstanceName ?? manifest.InstanceId ?? "未知实例"} · {manifest.CreatedAtUtc.ToLocalTime():MM-dd HH:mm:ss}";
        Subtitle = $"PID {manifest.ProcessId?.ToString() ?? "-"} · {localizer.Get("logs.isolation." + manifest.LogIsolation)}";
        State = manifest.State.ToString();

        var lines = new List<string>
        {
            $"{localizer.Get("logs.session")}  {manifest.SessionId}",
            $"{localizer.Get("logs.state")}    {manifest.State}",
            $"{localizer.Get("logs.created")}  {manifest.CreatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}",
            $"{localizer.Get("logs.process")}  {manifest.ProcessId?.ToString() ?? "-"}",
            $"{localizer.Get("logs.isolation")} {localizer.Get("logs.isolation." + manifest.LogIsolation)}",
            $"{localizer.Get("logs.evidence")} {manifest.Artifacts.Count}",
        };

        foreach (var artifact in manifest.Artifacts)
        {
            lines.Add($"  · {artifact.Kind} [{artifact.Ownership}]" +
                      (string.IsNullOrWhiteSpace(artifact.Warning) ? string.Empty : $" {artifact.Warning}"));
        }

        if (manifest.CaptureFault is not null)
        {
            lines.Add($"{localizer.Get("logs.captureFault")} {manifest.CaptureFault}");
        }

        foreach (var warning in manifest.Warnings)
        {
            lines.Add($"! {warning}");
        }

        _detail = string.Join(Environment.NewLine, lines);
        OnPropertyChanged(nameof(Detail));
    }
}
