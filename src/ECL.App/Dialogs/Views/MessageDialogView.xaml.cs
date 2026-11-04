// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows;
using System.Windows.Controls;
using Ecl.App.Dialogs.ViewModels;

namespace Ecl.App.Dialogs.Views;

public partial class MessageDialogView : UserControl
{
    public MessageDialogView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    /// <summary>
    /// 弹窗内容的焦点策略：破坏性操作把初始焦点放在取消按钮上，避免 Enter 误触发。
    /// 只设置一次，避免用户切换焦点后被重新抢走。
    /// </summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        if (DataContext is not MessageDialogViewModel viewModel)
        {
            return;
        }

        var target = viewModel.PreferredFocus switch
        {
            DialogInitialFocus.Cancel when viewModel.HasCancel => CancelButton,
            DialogInitialFocus.Secondary when viewModel.HasSecondary => SecondaryButton,
            _ => PrimaryButton,
        };

        target.Focus();
    }
}
