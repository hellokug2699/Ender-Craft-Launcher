// SPDX-License-Identifier: GPL-3.0-or-later

using CommunityToolkit.Mvvm.ComponentModel;
using Ecl.Core.Localization;
using Ecl.Minecraft.Launch;

namespace Ecl.App.ViewModels.Home;

/// <summary>
/// 主页启动卡中的一个步骤行。只承载呈现状态；真实结果由 <see cref="ILaunchStatusStore"/>
/// 与启动进度事件提供，本类不做任何完成判定。
/// </summary>
public sealed partial class HomeLaunchStepViewModel : ObservableObject
{
    private readonly ILocalizer _localizer;

    public HomeLaunchStepViewModel(string stepId, ILocalizer localizer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        ArgumentNullException.ThrowIfNull(localizer);

        StepId = stepId;
        _localizer = localizer;
        Title = localizer.Get(HomeLaunchPresentation.TitleKey(stepId));
        StateText = localizer.Get(HomeLaunchPresentation.StateKey(HomeLaunchStepState.Pending));
    }

    public string StepId { get; }

    public string Title { get; }

    [ObservableProperty]
    private HomeLaunchStepState _state = HomeLaunchStepState.Pending;

    /// <summary>0..100，供进度条使用；不显示百分比数字。</summary>
    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private string _stateText;

    [ObservableProperty]
    private string _message = string.Empty;

    /// <summary>步骤的真实耗时；没有结果时为空。</summary>
    [ObservableProperty]
    private string _durationText = string.Empty;

    /// <summary>可能卡住时的可观测证据说明；没有证据时为空。</summary>
    [ObservableProperty]
    private string _note = string.Empty;

    public bool IsRunning => State == HomeLaunchStepState.Running;

    public bool IsFailed => State == HomeLaunchStepState.Failed;

    public bool IsSuccess => State is HomeLaunchStepState.Success or HomeLaunchStepState.Warning;

    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);

    public bool HasNote => !string.IsNullOrWhiteSpace(Note);

    public void MarkPending() => Apply(HomeLaunchStepState.Pending);

    public void MarkNotRun() => Apply(HomeLaunchStepState.NotRun);

    public void MarkRunning() => Apply(HomeLaunchStepState.Running);

    /// <summary>更新运行中步骤的估量进度（0..1）；不改变步骤状态。</summary>
    public void UpdateElapsed(TimeSpan elapsed)
    {
        if (State != HomeLaunchStepState.Running)
        {
            return;
        }

        ProgressValue = HomeLaunchPresentation.RunningFraction(StepId, elapsed) * 100;
    }

    public void Complete(LaunchStepStatus status, string message, TimeSpan duration)
    {
        Message = message;
        Note = string.Empty;
        DurationText = duration > TimeSpan.Zero ? $"{duration.TotalSeconds:F1}s" : string.Empty;
        ProgressValue = 100;
        Apply(ToState(status));
    }

    /// <summary>从持久化快照还原一行（不进入运行状态，也不显示估量进度）。</summary>
    public void Restore(LaunchStatusStep step)
    {
        Message = step.Message;
        DurationText = step.Duration > TimeSpan.Zero ? $"{step.Duration.TotalSeconds:F1}s" : string.Empty;
        ProgressValue = 100;
        Apply(ToState(step.Status));
    }

    /// <summary>可能卡住时的提示；只陈述可观测事实，不断言卡死。</summary>
    public void SetLongRunningNote(TimeSpan elapsed, TimeSpan silentFor)
    {
        Note = _localizer.Get(
            "home.launch.step.longRunning",
            Math.Max(0, (int)elapsed.TotalSeconds),
            Math.Max(0, (int)silentFor.TotalSeconds));
    }

    public void ClearNote() => Note = string.Empty;

    private void Apply(HomeLaunchStepState state)
    {
        State = state;
        StateText = _localizer.Get(HomeLaunchPresentation.StateKey(state));
        if (state is HomeLaunchStepState.Pending or HomeLaunchStepState.NotRun or HomeLaunchStepState.Running)
        {
            ProgressValue = 0;
        }

        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(IsSuccess));
        OnPropertyChanged(nameof(HasMessage));
        OnPropertyChanged(nameof(HasNote));
    }

    private static HomeLaunchStepState ToState(LaunchStepStatus status) => status switch
    {
        LaunchStepStatus.Success => HomeLaunchStepState.Success,
        LaunchStepStatus.Warning => HomeLaunchStepState.Warning,
        LaunchStepStatus.Skipped => HomeLaunchStepState.Skipped,
        _ => HomeLaunchStepState.Failed,
    };
}
