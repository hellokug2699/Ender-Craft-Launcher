// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows;
using Ecl.Deployment;

namespace Ecl.Installer;

public enum IntegrityDecision
{
    Cancel,
    Confirmed,
    Skipped,
}

public partial class IntegrityWindow : Window
{
    private readonly InstallerIntegrityResult _integrity;

    public IntegrityWindow(InstallerIntegrityResult integrity)
    {
        InitializeComponent();
        _integrity = integrity;
        FileText.Text = $"{integrity.FileName}（{integrity.Version ?? "版本未知"}）";
        OfficialHashText.Text = integrity.OfficialSha256 ?? "官方摘要不可用";
        LocalHashText.Text = integrity.LocalSha256;
        CommandText.Text = integrity.PowerShellCommand;
        StatusText.Text = integrity.Message;
        ConfirmButton.IsEnabled = integrity.State == InstallerIntegrityState.Confirmed;
        if (integrity.State == InstallerIntegrityState.Mismatch)
        {
            StatusBorder.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2E, 0x1A, 0x21));
            StatusBorder.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4E, 0x27, 0x31));
            StatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF4, 0x79, 0x7F));
        }
    }

    public IntegrityDecision Decision { get; private set; } = IntegrityDecision.Cancel;

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        Decision = IntegrityDecision.Confirmed;
        DialogResult = true;
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        var accepted = MessageBox.Show(
            this,
            "你尚未核对本地文件与官方发布摘要。继续安装无法排除下载损坏、传输替换或文件被篡改的风险。\n\n确定跳过并继续吗？",
            "未验证安装文件",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;
        if (!accepted)
        {
            return;
        }

        Decision = IntegrityDecision.Skipped;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Decision = IntegrityDecision.Cancel;
        DialogResult = false;
    }
}
