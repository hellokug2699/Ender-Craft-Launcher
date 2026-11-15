// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.Core.Localization;
using Ecl.Core.Storage;

namespace Ecl.App.ViewModels;

/// <summary>
/// 首次启动的工作区引导（ADR 0018、ADR 0052 B10）：把原窗口 code-behind 中的建议读取、
/// 路径校验、旧版采用与“稍后配置”收敛到可测试的页面对象。窗口只负责显示、主题与系统目录选择。
///
/// 边界：只调用 <see cref="IWorkspaceService"/>；不自行创建工作区结构、不开始下载、不触碰实例目录。
/// 该流程发生在 Shell / Dialog Engine 宿主创建之前，因此仍是独立窗口。
/// </summary>
public sealed partial class WorkspaceSetupViewModel : ObservableObject
{
    private readonly IWorkspaceService _workspace;
    private readonly ILocalizer _localizer;
    private int _validationVersion;

    public WorkspaceSetupViewModel(IWorkspaceService workspace, ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(localizer);

        _workspace = workspace;
        _localizer = localizer;
    }

    /// <summary>窗口根据该结果设置 <c>DialogResult</c> 并关闭；<c>true</c> 表示工作区已就绪。</summary>
    public event EventHandler<bool>? Completed;

    public ObservableCollection<WorkspaceSuggestionItem> Suggestions { get; } = [];

    [ObservableProperty]
    private WorkspaceSuggestionItem? _selectedSuggestion;

    [ObservableProperty]
    private string _pathText = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _hasLegacy;

    [ObservableProperty]
    private string _legacySummary = string.Empty;

    public bool CanAccept => !IsBusy && !string.IsNullOrWhiteSpace(PathText);

    public bool HasSuggestions => Suggestions.Count > 0;

    public string Title => _localizer.Get("workspace.setup.title");

    public string Description => _localizer.Get("workspace.setup.description");

    public string PathLabel => _localizer.Get("workspace.setup.path");

    public string BrowseText => _localizer.Get("workspace.setup.browse");

    public string SuggestionsLabel => _localizer.Get("workspace.setup.suggestions");

    public string LegacyTitle => _localizer.Get("workspace.setup.legacyTitle");

    public string LegacyAdoptText => _localizer.Get("workspace.setup.legacyAdopt");

    public string AcceptText => _localizer.Get("workspace.setup.accept");

    public string LaterText => _localizer.Get("workspace.setup.later");

    /// <summary>读取推荐位置与旧版数据探测结果；不写盘。</summary>
    public void Initialize()
    {
        Suggestions.Clear();

        var legacy = _workspace.DetectLegacy();
        HasLegacy = legacy.Found;
        LegacySummary = legacy.Found
            ? _localizer.Get("workspace.setup.legacySummary", legacy.Path, legacy.InstanceCount, legacy.VersionCount)
            : string.Empty;

        foreach (var suggestion in _workspace.Suggest())
        {
            Suggestions.Add(new WorkspaceSuggestionItem(
                suggestion.Path,
                _localizer.Get(suggestion.ReasonCode)));
        }

        OnPropertyChanged(nameof(HasSuggestions));

        PathText = _workspace.Primary?.Path
                   ?? Suggestions.FirstOrDefault()?.Path
                   ?? string.Empty;
        SelectedSuggestion = Suggestions.FirstOrDefault(item =>
            string.Equals(item.Path, PathText, StringComparison.Ordinal));
    }

    /// <summary>
    /// 以用户当前输入执行一次只读路径校验；结果只进入 <see cref="StatusText"/>。
    /// 多次输入并发时只允许最后一次结果回写（与旧窗口的版本计数语义一致）。
    /// </summary>
    public async Task ValidateAsync(CancellationToken cancellationToken = default)
    {
        var path = PathText.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            StatusText = string.Empty;
            return;
        }

        var version = ++_validationVersion;
        WorkspacePathCheck check;
        try
        {
            check = await Task.Run(() => _workspace.Check(path, createIfMissing: false), cancellationToken)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (version == _validationVersion)
            {
                StatusText = $"{_localizer.Get("workspace.setup.error")}：{exception.Message}";
            }

            return;
        }

        if (version != _validationVersion)
        {
            return;
        }

        if (check.Status == WorkspaceStatus.Missing)
        {
            StatusText = _localizer.Get("workspace.setup.willCreate");
            return;
        }

        if (!check.IsUsable)
        {
            StatusText = $"{_localizer.Get("workspace.setup.error")}：{check.ProblemMessage}";
            return;
        }

        var lines = new List<string> { _localizer.Get("workspace.setup.ready") };
        lines.AddRange(check.WarningCodes.Select(_localizer.Get));
        if (check.FreeBytes is { } free)
        {
            lines.Add(_localizer.Get("workspace.setup.freeSpace", FormatBytes(free)));
        }

        StatusText = string.Join(Environment.NewLine, lines);
    }

    [RelayCommand]
    private async Task AcceptAsync()
    {
        if (IsBusy || string.IsNullOrWhiteSpace(PathText))
        {
            return;
        }

        SetBusy(true);
        try
        {
            var result = await _workspace.InitializeAsync(PathText.Trim()).ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                StatusText = $"{_localizer.Get("workspace.setup.error")}：{result.Error?.Message}";
                return;
            }

            Completed?.Invoke(this, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusText = $"{_localizer.Get("workspace.setup.error")}：{exception.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    [RelayCommand]
    private async Task AdoptLegacyAsync()
    {
        if (IsBusy || !HasLegacy)
        {
            return;
        }

        SetBusy(true);
        try
        {
            var result = await _workspace.AdoptLegacyAsync().ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                StatusText = $"{_localizer.Get("workspace.setup.error")}：{result.Error?.Message}";
                return;
            }

            Completed?.Invoke(this, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusText = $"{_localizer.Get("workspace.setup.error")}：{exception.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    [RelayCommand]
    private async Task SkipAsync()
    {
        if (IsBusy)
        {
            return;
        }

        SetBusy(true);
        try
        {
            await _workspace.SkipOnboardingAsync().ConfigureAwait(true);
            Completed?.Invoke(this, false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusText = $"{_localizer.Get("workspace.setup.error")}：{exception.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    partial void OnPathTextChanged(string value) => OnPropertyChanged(nameof(CanAccept));

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanAccept));

    partial void OnSelectedSuggestionChanged(WorkspaceSuggestionItem? value)
    {
        if (value is not null && !string.Equals(value.Path, PathText, StringComparison.Ordinal))
        {
            PathText = value.Path;
        }
    }

    private void SetBusy(bool busy) => IsBusy = busy;

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024 * 1024):F1} GB",
        >= 1024L * 1024 => $"{bytes / (1024d * 1024):F1} MB",
        _ => $"{bytes} B",
    };
}

/// <summary>推荐位置：路径与本地化后的推荐原因。</summary>
public sealed record WorkspaceSuggestionItem(string Path, string Reason);
