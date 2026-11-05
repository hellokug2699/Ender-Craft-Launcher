// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows;
using System.Windows.Controls;
using Ecl.App.Workspace.ViewModels;

namespace Ecl.App.Views;

public partial class InstanceDetailView : UserControl
{
    /// <summary>
    /// 低于此可用宽度时，左侧二级导航折叠为内容区顶部的页面选择器（ADR 0050 响应式约束）。
    /// 1080×720 且全局导航与 Inspector 同时展开时，工作区页面可用宽度约为 480。
    /// </summary>
    private const double CompactNavigationThreshold = 640;

    public InstanceDetailView()
    {
        InitializeComponent();
        SizeChanged += OnPageSizeChanged;
        DataContextChanged += (_, _) => UpdateNavigationMode(ActualWidth);
    }

    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateNavigationMode(e.NewSize.Width);

    private void UpdateNavigationMode(double availableWidth)
    {
        if (DataContext is InstanceDetailPageViewModel page)
        {
            page.IsNavigationCollapsed = availableWidth < CompactNavigationThreshold;
        }
    }
}
