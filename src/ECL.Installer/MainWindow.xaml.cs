// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace Ecl.Installer;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        InstallPathBox.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "Ender Craft Launcher");
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择 ECL 安装目录", InitialDirectory = Directory.Exists(InstallPathBox.Text) ? InstallPathBox.Text : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
        if (dialog.ShowDialog(this) == true) InstallPathBox.Text = dialog.FolderName;
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        InstallButton.IsEnabled = false;
        InstallProgress.Visibility = Visibility.Visible;
        InstallProgress.IsIndeterminate = true;
        try
        {
            var root = Path.GetFullPath(InstallPathBox.Text.Trim());
            if (string.IsNullOrWhiteSpace(root)) throw new InvalidOperationException("请先选择安装目录。");
            var source = AppContext.BaseDirectory;
            var bootstrapper = Path.Combine(source, "ECL.Bootstrapper.exe");
            var manifest = Path.Combine(source, "update-manifest.json");
            var signature = Path.Combine(source, "update-manifest.sig");
            var package = Path.Combine(source, "ECL-package.zip");
            var trust = Path.Combine(source, "update-trust.json");
            foreach (var required in new[] { bootstrapper, manifest, signature, package, trust })
                if (!File.Exists(required)) throw new FileNotFoundException("安装介质不完整，缺少文件：" + Path.GetFileName(required), required);

            Directory.CreateDirectory(root);
            File.Copy(bootstrapper, Path.Combine(root, "ECL.Bootstrapper.exe"), overwrite: true);
            File.Copy(trust, Path.Combine(root, "update-trust.json"), overwrite: true);
            StatusText.Text = "正在启动独立部署程序，验证签名并安装版本…";
            var start = new ProcessStartInfo(Path.Combine(root, "ECL.Bootstrapper.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = root
            };
            start.ArgumentList.Add("--install-root"); start.ArgumentList.Add(root);
            start.ArgumentList.Add("--apply");
            start.ArgumentList.Add("--manifest"); start.ArgumentList.Add(manifest);
            start.ArgumentList.Add("--signature"); start.ArgumentList.Add(signature);
            start.ArgumentList.Add("--package"); start.ArgumentList.Add(package);
            start.ArgumentList.Add("--trust"); start.ArgumentList.Add(Path.Combine(root, "update-trust.json"));
            start.ArgumentList.Add("--deploy-only");
            using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 ECL Bootstrapper。");
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new InvalidOperationException($"部署失败，Bootstrapper 退出码：{process.ExitCode}。请检查安装目录中的 bootstrapper.log。");
            StatusText.Text = "ECL 已成功安装。你可以关闭安装程序并启动 ECL.Bootstrapper.exe。";
            InstallProgress.IsIndeterminate = false; InstallProgress.Value = 100;
            InstallButton.Content = "安装完成";
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            MessageBox.Show(this, ex.Message, "ECL 安装失败", MessageBoxButton.OK, MessageBoxImage.Error);
            InstallButton.IsEnabled = true;
        }
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
}
