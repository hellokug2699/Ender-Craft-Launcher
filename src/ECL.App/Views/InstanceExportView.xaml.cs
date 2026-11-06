// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows;
using System.Windows.Controls;
using Ecl.App.Workspace.ViewModels;

namespace Ecl.App.Views;

/// <summary>
/// 导出页没有视图级业务逻辑：扫描、导出与进度都在 ViewModel/领域服务中。
/// 首次显示时才触发整实例扫描，避免打开工作区就遍历全部实例文件。
/// </summary>
public partial class InstanceExportView : UserControl
{
    public InstanceExportView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is InstanceExportViewModel viewModel)
        {
            await viewModel.InitializeAsync();
        }
    }
}
