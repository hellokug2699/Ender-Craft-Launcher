// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows;
using System.Windows.Controls;
using Ecl.App.ViewModels.Settings;

namespace Ecl.App.Views;

/// <summary>
/// 设置中心「网络」页。密码从 <see cref="PasswordBox.Password"/> 直接交给页面对象，
/// 不经过绑定属性；保存成功后清空输入框。切换二级页时把滚动位置复位到顶部。
/// </summary>
public partial class SettingsNetworkPageView : UserControl
{
    public SettingsNetworkPageView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => PageScroll?.ScrollToTop();
    }

    private void SaveCredential_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsNetworkPageViewModel viewModel
            && viewModel.SaveCredential(PasswordInput.Password))
        {
            PasswordInput.Clear();
        }
    }
}
