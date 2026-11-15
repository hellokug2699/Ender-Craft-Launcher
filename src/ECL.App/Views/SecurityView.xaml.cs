// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows.Controls;

namespace Ecl.App.Views;

/// <summary>
/// 安全区域容器视图。二级页切换只替换内容控件，由页面对象决定是否加载数据。
/// </summary>
public partial class SecurityView : UserControl
{
    public SecurityView() => InitializeComponent();
}
