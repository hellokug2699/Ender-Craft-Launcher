// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows;
using System.Windows.Controls;
using Ecl.App.Dialogs.ViewModels;

namespace Ecl.App.Dialogs.Views;

public partial class ModifyConfirmDialogView : UserControl
{
    public ModifyConfirmDialogView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    /// <summary>
    /// 变更会写入实例目录，属破坏性操作：初始焦点固定在取消按钮上，
    /// 避免 Enter 直接触发执行。只设置一次，之后不再抢焦点。
    /// </summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        if (DataContext is not ModifyConfirmDialogViewModel)
        {
            return;
        }

        CancelButton.Focus();
    }
}
