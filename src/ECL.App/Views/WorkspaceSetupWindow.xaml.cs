// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO;
using System.Windows;
using System.Windows.Threading;
using Ecl.App.ViewModels;
using Microsoft.Win32;

namespace Ecl.App.Views;

/// <summary>
/// 首次启动的工作区引导窗口（ADR 0018、ADR 0052 B10）。窗口不再持有业务逻辑：
/// 建议列表、路径校验、采用旧版与稍后配置都在 <see cref="WorkspaceSetupViewModel"/> 中。
/// 这里只负责主题化显示、输入去抖与系统目录选择器。
/// </summary>
public partial class WorkspaceSetupWindow : Window
{
    private readonly WorkspaceSetupViewModel _viewModel;
    private readonly DispatcherTimer _debounce;

    public WorkspaceSetupWindow(WorkspaceSetupViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _debounce.Tick += async (_, _) =>
        {
            _debounce.Stop();
            await _viewModel.ValidateAsync();
        };

        viewModel.Completed += OnCompleted;
        Loaded += OnLoaded;
        Closed += OnClosed;
        viewModel.Initialize();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.ValidateAsync();
        PathBox.Focus();
    }

    private void OnClosed(object? sender, EventArgs e) => _viewModel.Completed -= OnCompleted;

    private void OnCompleted(object? sender, bool accepted)
    {
        DialogResult = accepted;
        Close();
    }

    private void PathBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _debounce.Stop();
        _debounce.Start();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = _viewModel.Title,
            InitialDirectory = Directory.Exists(_viewModel.PathText)
                ? _viewModel.PathText
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };

        if (dialog.ShowDialog(this) == true)
        {
            _viewModel.PathText = dialog.FolderName;
        }
    }
}
