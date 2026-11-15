// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows;
using System.Windows.Controls;
using Ecl.App.ViewModels;

namespace Ecl.App.Views;

public partial class SettingsView : UserControl
{
    /// <summary>
    /// 低于此可用宽度时，设置中心的左侧二级导航折叠为内容区顶部的分类选择器
    /// （ADR 0050 响应式约束，与实例工作区一致）。
    /// </summary>
    private const double CompactNavigationThreshold = 640;

    public SettingsView()
    {
        InitializeComponent();
        SizeChanged += OnPageSizeChanged;
        DataContextChanged += (_, _) => UpdateNavigationMode(ActualWidth);
    }

    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateNavigationMode(e.NewSize.Width);

    private void UpdateNavigationMode(double availableWidth)
    {
        if (DataContext is SettingsViewModel viewModel)
        {
            viewModel.IsNavigationCollapsed = availableWidth < CompactNavigationThreshold;
        }
    }
}
