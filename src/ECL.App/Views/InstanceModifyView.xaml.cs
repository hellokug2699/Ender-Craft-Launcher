// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows;
using System.Windows.Controls;
using Ecl.App.Workspace.ViewModels;

namespace Ecl.App.Views;

/// <summary>
/// 「修改」页视图。首次显示时读取目标版本目录；页面本身不包含业务判断，
/// 状态与错误由 <see cref="InstanceModifyViewModel"/> 按真实领域结果呈现。
/// </summary>
public partial class InstanceModifyView : UserControl
{
    public InstanceModifyView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not InstanceModifyViewModel page)
        {
            return;
        }

        // VM 内部捕获并记录失败，不产生未观察的异常；这里只需等待初始化结束。
        await page.EnsureLoadedAsync().ConfigureAwait(true);
    }
}
