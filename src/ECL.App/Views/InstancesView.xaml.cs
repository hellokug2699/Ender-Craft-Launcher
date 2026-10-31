// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows;
using System.Windows.Controls;
using Ecl.App.ViewModels;

namespace Ecl.App.Views;

public partial class InstancesView : UserControl
{
    public InstancesView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 密码只从 PasswordBox 直接交给 ViewModel 的登录调用，不进入绑定、不落盘。
    /// </summary>
    private async void OnThirdPartyLoginClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not InstancesViewModel viewModel)
        {
            return;
        }

        var password = ThirdPartyPassword.Password;
        ThirdPartyPassword.Clear();
        await viewModel.SignInThirdPartyAsync(password);
    }
}
