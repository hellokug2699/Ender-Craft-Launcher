// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows.Controls;

namespace Ecl.App.Dialogs.Views;

/// <summary>“已经选择的目录”弹窗视图；逻辑全部在 ViewModel，code-behind 只做初始化。</summary>
public partial class InstanceDirectoryDialogView : UserControl
{
    public InstanceDirectoryDialogView()
    {
        InitializeComponent();
    }
}
