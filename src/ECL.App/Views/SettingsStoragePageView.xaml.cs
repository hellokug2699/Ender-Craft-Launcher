// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows.Controls;

namespace Ecl.App.Views;

/// <summary>
/// 设置中心「存储与路径」页。切换二级页时把滚动位置复位到页面顶部；
/// 页面自身不复制/移动文件，迁移全部由 <c>IWorkspaceMigrationService</c> 执行。
/// </summary>
public partial class SettingsStoragePageView : UserControl
{
    public SettingsStoragePageView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => PageScroll?.ScrollToTop();
    }
}
