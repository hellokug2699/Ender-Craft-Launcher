// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows.Controls;

namespace Ecl.App.Views;

/// <summary>投影页没有视图级业务逻辑：文件读取、删除与支持检测都在 ViewModel/领域服务中。</summary>
public partial class InstanceSchematicsView : UserControl
{
    public InstanceSchematicsView()
    {
        InitializeComponent();
    }
}
