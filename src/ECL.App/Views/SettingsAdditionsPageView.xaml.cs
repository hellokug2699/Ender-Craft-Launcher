// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows.Controls;

namespace Ecl.App.Views;

/// <summary>
/// 设置中心「扩展管理」页。切换二级页时把滚动位置复位到页面顶部；
/// 页面自身不读写扩展状态、不发起网络请求，这些都在领域服务与页面对象中完成。
/// </summary>
public partial class SettingsAdditionsPageView : UserControl
{
    public SettingsAdditionsPageView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => PageScroll?.ScrollToTop();
    }
}
