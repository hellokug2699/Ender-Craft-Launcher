// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows.Controls;

namespace Ecl.App.Views;

/// <summary>导出页没有视图级业务逻辑：扫描、导出与进度都在 ViewModel/领域服务中。</summary>
public partial class InstanceExportView : UserControl
{
    public InstanceExportView()
    {
        InitializeComponent();
    }
}
