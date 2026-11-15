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
        // 不自动填入与 EXE 同目录的摘要；用户必须从官方 Release 独立取得并粘贴。
        OfficialHashText.Text = string.Empty;
        OfficialHashText.ToolTip = "粘贴对应 Release 的 .sha256 资产中的 64 位 SHA-256 摘要。";
        OfficialHashText.TextChanged += (_, _) => UpdateIntegrityState();
        LocalHashText.Text = integrity.LocalSha256;
        CommandText.Text = integrity.PowerShellCommand;
        StatusText.Text = integrity.Message;
        ConfirmButton.IsEnabled = false;
        UpdateIntegrityState();
        if (integrity.State == InstallerIntegrityState.Mismatch)
        {
            StatusBorder.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2E, 0x1A, 0x21));
            StatusBorder.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4E, 0x27, 0x31));
            StatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF4, 0x79, 0x7F));
        }
    }

    public IntegrityDecision Decision { get; private set; } = IntegrityDecision.Cancel;

    private void UpdateIntegrityState()
    {
        var officialHash = OfficialHashText.Text.Trim();
        var validFormat = officialHash.Length == 64 && officialHash.All(Uri.IsHexDigit);
        var matches = validFormat &&
                      string.Equals(officialHash, _integrity.LocalSha256, StringComparison.OrdinalIgnoreCase);
        ConfirmButton.IsEnabled = matches;

        if (matches)
        {
            StatusText.Text = "官方摘要与程序计算的本机文件哈希一致。请仍使用 PowerShell 独立计算并逐字符核对后再确认。";
            StatusText.Foreground = System.Windows.Media.Brushes.LightGreen;
        }
        else if (officialHash.Length > 0)
        {
            StatusText.Text = validFormat
                ? "摘要不匹配。请取消安装并重新从官方 Release 下载。"
                : "摘要格式无效：需要 64 位十六进制 SHA-256。";
            StatusText.Foreground = System.Windows.Media.Brushes.OrangeRed;
        }
        else
        {
            StatusText.Text = _integrity.Message;
        }
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmButton.IsEnabled)
        {
            return;
        }

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
