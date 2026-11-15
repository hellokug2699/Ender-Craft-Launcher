// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows.Controls;

namespace Ecl.App.Dialogs.Views;

/// <summary>
/// 版本导入弹窗内容视图。视图不含业务逻辑；执行、取消与结果提交都由
/// <see cref="ViewModels.VersionImportDialogViewModel"/> 与会话契约负责。
/// </summary>
public partial class VersionImportDialogView : UserControl
{
    public VersionImportDialogView() => InitializeComponent();
}
