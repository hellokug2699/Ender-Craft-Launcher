// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows.Controls;

namespace Ecl.App.Views;

/// <summary>
/// 状态中心视图。页面只呈现真实检查结论；没有行级修复、启动、停止或删除按钮。
/// </summary>
public partial class RuntimeStatusView : UserControl
{
    public RuntimeStatusView() => InitializeComponent();
}
