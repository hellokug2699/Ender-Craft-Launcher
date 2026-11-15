// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows.Controls;

namespace Ecl.App.Views;

/// <summary>
/// 设置中心「界面偏好」页。切换二级页时把滚动位置复位到页面顶部，
/// 避免上一页的滚动偏移把字体分组截断；页面自身不读取配置或改变缩放。
/// </summary>
public partial class SettingsInterfacePageView : UserControl
{
    public SettingsInterfacePageView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => PageScroll?.ScrollToTop();
    }
}
