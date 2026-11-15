// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows.Controls;
using Ecl.App.ViewModels.Security;

namespace Ecl.App.Views;

/// <summary>
/// 安全中心视图。页面不含业务逻辑；「解决问题」只把分类结果滚入视野，
/// 具体忽略/删除仍由各条目的命令与 Dialog Engine 执行。
/// </summary>
public partial class SecurityCenterView : UserControl
{
    private SecurityCenterViewModel? _viewModel;

    public SecurityCenterView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += OnUnloaded;
    }

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        Detach();
        if (e.NewValue is SecurityCenterViewModel viewModel)
        {
            _viewModel = viewModel;
            viewModel.FindingsFocusRequested += OnFindingsFocusRequested;
        }
    }

    private void OnUnloaded(object sender, System.Windows.RoutedEventArgs e) => Detach();

    private void Detach()
    {
        if (_viewModel is not null)
        {
            _viewModel.FindingsFocusRequested -= OnFindingsFocusRequested;
            _viewModel = null;
        }
    }

    private void OnFindingsFocusRequested(object? sender, EventArgs e) => FindingsSection.BringIntoView();
}
