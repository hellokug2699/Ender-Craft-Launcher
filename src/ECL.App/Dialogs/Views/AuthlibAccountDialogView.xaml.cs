// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows;
using System.Windows.Controls;
using Ecl.App.Dialogs.ViewModels;

namespace Ecl.App.Dialogs.Views;

/// <summary>
/// Authlib 账户弹窗内容视图。密码只从 <see cref="PasswordBox.Password"/> 直接交给页面对象，
/// 不经过绑定属性；视图不含认证、服务器或凭据持久化逻辑。
/// </summary>
public partial class AuthlibAccountDialogView : UserControl
{
    public AuthlibAccountDialogView() => InitializeComponent();

    private async void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is AuthlibAccountDialogViewModel viewModel)
        {
            await viewModel.SubmitAsync(PasswordInput.Password);
        }
    }
}
