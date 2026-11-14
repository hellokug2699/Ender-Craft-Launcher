// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using Ecl.App.Services;
using Ecl.App.Shell.Output;
using Ecl.App.ViewModels;
using Ecl.App.ViewModels.Home;
using Ecl.Core;
using Ecl.Core.Localization;
using Ecl.Minecraft.Instances;
using Ecl.Minecraft.Launch;
using Microsoft.Extensions.Logging;

namespace Ecl.App.Tests.Support;

/// <summary>主页（B5）测试共用替身：不依赖真实实例服务、启动服务或磁盘上的实例记录。</summary>
internal static class HomeTestData
{
    public static ILocalizer Localizer { get; } = new JsonLocalizer(CultureInfo.GetCultureInfo("zh-CN"));

    public static AppPaths CreatePaths() =>
        new(Path.Combine(Path.GetTempPath(), "ecl-home-tests", Guid.NewGuid().ToString("N")));

    public static InstanceItemViewModel Instance(
        string id,
        IAppPaths paths,
        string? name = null,
        string loader = "fabric",
        string? loaderVersion = "0.16.0") =>
        new(
            new GameInstance
            {
                Id = id,
                Name = name ?? id,
                GameVersion = "1.21.1",
                VersionId = $"1.21.1-{id}",
                Loader = loader,
                LoaderVersion = loaderVersion,
            },
            Localizer,
            paths);

    public static LaunchStatusSnapshot Snapshot(
        string instanceId,
        LaunchAttemptState state,
        string? sessionId = null,
        DateTimeOffset? startedAt = null)
    {
        var started = startedAt ?? DateTimeOffset.UtcNow;
        return new LaunchStatusSnapshot
        {
            InstanceId = instanceId,
            StartedAtUtc = started,
            CompletedAtUtc = state == LaunchAttemptState.Running ? null : started.AddSeconds(2),
            State = state,
            SessionId = sessionId,
            FailureSummary = state == LaunchAttemptState.Failed ? "缺少游戏文件" : null,
            Duration = TimeSpan.FromSeconds(2),
            Steps = state == LaunchAttemptState.Running
                ? []
                :
                [
                    new LaunchStatusStep
                    {
                        StepId = "validate",
                        Status = LaunchStepStatus.Success,
                        Message = "ok",
                    },
                    new LaunchStatusStep
                    {
                        StepId = "files",
                        Status = state == LaunchAttemptState.Failed
                            ? LaunchStepStatus.Failed
                            : LaunchStepStatus.Success,
                        Message = state == LaunchAttemptState.Failed ? "缺少游戏文件" : "ok",
                    },
                ],
        };
    }
}

internal sealed class FakeLaunchService(params string[] steps) : ILaunchService
{
    public IReadOnlyList<string> Steps { get; } = steps;

    public Task<LaunchReport> LaunchAsync(
        string instanceId,
        bool dryRun,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("主页测试不使用该重载。");

    public Task<LaunchReport> LaunchAsync(
        string instanceId,
        bool dryRun,
        bool acceptJavaRisk,
        bool acceptSecurityRisk = false,
        CancellationToken cancellationToken = default,
        IProgress<LaunchStepProgress>? progress = null) =>
        throw new NotSupportedException("主页测试的启动动作由 IHomeLaunchHost 替身提供。");
}

internal sealed class FakeLaunchStatusStore : ILaunchStatusStore
{
    private readonly Dictionary<string, LaunchStatusSnapshot> _snapshots = new(StringComparer.Ordinal);

    public LaunchStatusSnapshot? Get(string instanceId) =>
        _snapshots.TryGetValue(instanceId, out var snapshot) ? snapshot : null;

    public void RecordStarted(string instanceId) =>
        _snapshots[instanceId] = new LaunchStatusSnapshot
        {
            InstanceId = instanceId,
            StartedAtUtc = DateTimeOffset.UtcNow,
            State = LaunchAttemptState.Running,
        };

    public void RecordCompleted(string instanceId, LaunchReport report) => throw new NotSupportedException();

    /// <summary>测试用：直接写入期望的快照。</summary>
    public void Seed(LaunchStatusSnapshot snapshot) => _snapshots[snapshot.InstanceId] = snapshot;
}

internal sealed class FakeLaunchHost : IHomeLaunchHost
{
    public bool IsBusy { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>测试用启动动作；由测试决定进度事件、日志与写入的状态快照。</summary>
    public Func<InstanceItemViewModel, InstanceOperationState, bool, IProgress<LaunchStepProgress>?, Task>? OnLaunch
    {
        get;
        set;
    }

    public Task LaunchAsync(
        InstanceItemViewModel instance,
        InstanceOperationState state,
        bool dryRun,
        IProgress<LaunchStepProgress>? progress) =>
        OnLaunch?.Invoke(instance, state, dryRun, progress) ?? Task.CompletedTask;

    public void RaiseBusyChanged() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsBusy)));
}

internal sealed class FakeInstanceSource : IHomeInstanceSource
{
    public ObservableCollection<InstanceItemViewModel> Items { get; } = [];

    public IReadOnlyList<InstanceItemViewModel> Instances => Items;

    public InstanceItemViewModel? SelectedInstance { get; set; }

    public bool HasInstanceLoadError { get; set; }

    public event NotifyCollectionChangedEventHandler? InstancesChanged;

    public event PropertyChangedEventHandler? PropertyChanged;

    public FakeInstanceSource()
    {
        Items.CollectionChanged += (sender, args) => InstancesChanged?.Invoke(this, args);
    }

    public void RaiseSelectedChanged() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedInstance)));

    public void RaiseLoadErrorChanged() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasInstanceLoadError)));
}

internal sealed class FakeHomeLayoutStore : IHomeLayoutStore
{
    private List<string?> _slots = [];

    public int SaveCount { get; private set; }

    public IReadOnlyList<string?> Load() => [.. _slots];

    public void Save(IReadOnlyList<string?> slots)
    {
        _slots = [.. slots];
        SaveCount++;
    }

    public void Seed(params string?[] slots) => _slots = [.. slots];
}

internal sealed class TestAppLogBuffer : IDisposable
{
    private readonly AppLogBuffer _buffer = new();
    private readonly ILogger _logger;

    public TestAppLogBuffer()
    {
        _logger = _buffer.CreateLogger("Test");
    }

    public AppLogBuffer Buffer => _buffer;

    public void Write(LogLevel level, string message) => _logger.Log(level, "{Message}", message);

    public void Dispose() => _buffer.Dispose();
}

