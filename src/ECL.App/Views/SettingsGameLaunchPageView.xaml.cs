// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows.Controls;

namespace Ecl.App.Views;

/// <summary>
/// 设置中心「游戏与启动」页。切换二级页时把滚动位置复位到页面顶部；
/// 页面自身不读取配置文件、不计算自动内存，也不改变正在运行的游戏。
/// </summary>
public partial class SettingsGameLaunchPageView : UserControl
{
    public SettingsGameLaunchPageView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => PageScroll?.ScrollToTop();
    }
}
