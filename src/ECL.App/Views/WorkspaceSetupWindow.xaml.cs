// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Ecl.Core.Localization;
using Ecl.Core.Storage;
using Microsoft.Win32;

namespace Ecl.App.Views;

/// <summary>
/// 首次启动的工作区引导（ADR 0018）。只负责展示建议、校验路径并调用
/// <see cref="IWorkspaceService"/>；不自行创建实例结构、不开始下载。
/// </summary>
public partial class WorkspaceSetupWindow : Window
{
    private readonly IWorkspaceService _workspace;
    private readonly ILocalizer _localizer;
    private readonly DispatcherTimer _debounce;
    private readonly ObservableCollection<WorkspaceSuggestionItem> _suggestions = [];
    private bool _busy;
    private int _validationVersion;

    public WorkspaceSetupWindow(IWorkspaceService workspace, ILocalizer localizer)
    {
        InitializeComponent();
        _workspace = workspace;
        _localizer = localizer;
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            _ = ValidateAsync();
        };

        SuggestionsList.ItemsSource = _suggestions;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var legacy = _workspace.DetectLegacy();
        if (legacy.Found)
        {
            LegacyCard.Visibility = Visibility.Visible;
            LegacyPathText.Text = $"{legacy.Path}\n实例 {legacy.InstanceCount} 个，版本 {legacy.VersionCount} 个";
        }

        foreach (var suggestion in _workspace.Suggest())
        {
            _suggestions.Add(new WorkspaceSuggestionItem(
                suggestion.Path,
                _localizer.Get(suggestion.ReasonCode)));
        }

        PathBox.Text = _workspace.Primary?.Path
                       ?? _suggestions.FirstOrDefault()?.Path
                       ?? string.Empty;

        if (_suggestions.Count > 0)
        {
            SuggestionsList.SelectedIndex = 0;
        }
    }

    private void Suggestions_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (SuggestionsList.SelectedItem is WorkspaceSuggestionItem item)
        {
            PathBox.Text = item.Path;
        }
    }

    private void PathBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        AcceptButton.IsEnabled = !string.IsNullOrWhiteSpace(PathBox.Text);
        _debounce.Stop();
        _debounce.Start();
    }

    private async Task ValidateAsync()
    {
        var path = PathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            StatusText.Text = string.Empty;
            return;
        }

        var version = ++_validationVersion;
        WorkspacePathCheck check;
        try
        {
            check = await Task.Run(() => _workspace.Check(path, createIfMissing: false));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"{_localizer.Get("workspace.setup.error")}：{exception.Message}";
            return;
        }

        if (version != _validationVersion)
        {
            return;
        }

        if (check.Status == WorkspaceStatus.Missing)
        {
            StatusText.Text = _localizer.Get("workspace.setup.willCreate");
            return;
        }

        if (!check.IsUsable)
        {
            StatusText.Text = $"{_localizer.Get("workspace.setup.error")}：{check.ProblemMessage}";
            return;
        }

        var lines = new List<string> { _localizer.Get("workspace.setup.ready") };
        lines.AddRange(check.WarningCodes.Select(_localizer.Get));
        if (check.FreeBytes is { } free)
        {
            lines.Add($"可用空间：{FormatBytes(free)}");
        }

        StatusText.Text = string.Join(Environment.NewLine, lines);
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = _localizer.Get("workspace.setup.title"),
            InitialDirectory = Directory.Exists(PathBox.Text)
                ? PathBox.Text
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };

        if (dialog.ShowDialog(this) == true)
        {
            PathBox.Text = dialog.FolderName;
        }
    }

    private async void Accept_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        AcceptButton.IsEnabled = false;
        try
        {
            var result = await _workspace.InitializeAsync(PathBox.Text);
            if (!result.IsSuccess)
            {
                StatusText.Text = $"{_localizer.Get("workspace.setup.error")}：{result.Error?.Message}";
                return;
            }

            DialogResult = true;
            Close();
        }
        finally
        {
            _busy = false;
            AcceptButton.IsEnabled = true;
        }
    }

    private async void AdoptLegacy_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        try
        {
            var result = await _workspace.AdoptLegacyAsync();
            if (!result.IsSuccess)
            {
                StatusText.Text = $"{_localizer.Get("workspace.setup.error")}：{result.Error?.Message}";
                return;
            }

            DialogResult = true;
            Close();
        }
        finally
        {
            _busy = false;
        }
    }

    private async void Later_Click(object sender, RoutedEventArgs e)
    {
        await _workspace.SkipOnboardingAsync();
        DialogResult = false;
        Close();
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024 * 1024):F1} GB",
        >= 1024L * 1024 => $"{bytes / (1024d * 1024):F1} MB",
        _ => $"{bytes} B",
    };

    private sealed record WorkspaceSuggestionItem(string Path, string Reason);
}
