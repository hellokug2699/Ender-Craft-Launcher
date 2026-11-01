// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Ecl.Deployment;
using Microsoft.Win32;

namespace Ecl.Installer;

public partial class MainWindow : Window
{
    private readonly InstallerService _installer = new();
    private readonly DispatcherTimer _logTimer;
    private CancellationTokenSource? _cancellation;
    private string? _installRoot;
    private string? _logPath;
    private InstallMedia? _media;

    public MainWindow()
    {
        InitializeComponent();
        InstallPathBox.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "Ender Craft Launcher");

        _logTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _logTimer.Tick += (_, _) => RefreshLog();
        Loaded += (_, _) => InspectMedia();
    }

    private void InspectMedia()
    {
        var result = InstallMedia.Inspect(AppContext.BaseDirectory);
        if (!result.IsSuccess)
        {
            _media = null;
            InstallButton.IsEnabled = false;
            MediaStatusText.Text = $"安装介质不可用：{result.Error!.Message}";
            return;
        }

        _media = result.Value;
        MediaStatusText.Text = $"安装介质已就绪：{Path.GetFileName(result.Value!.PackagePath)}";
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择 ECL 安装目录",
            InitialDirectory = Directory.Exists(InstallPathBox.Text)
                ? InstallPathBox.Text
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        if (dialog.ShowDialog(this) == true)
        {
            InstallPathBox.Text = dialog.FolderName;
        }
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_media is null)
        {
            MessageBox.Show(this, "安装介质不完整，请使用完整的 ECL 安装程序。", "无法安装", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _installRoot = PathBoxValue();
        if (string.IsNullOrWhiteSpace(_installRoot))
        {
            StatusText.Text = "请先选择安装目录。";
            return;
        }

        _cancellation = new CancellationTokenSource();
        InstallButton.IsEnabled = false;
        CancelButton.Visibility = Visibility.Visible;
        InstallProgress.Visibility = Visibility.Visible;
        InstallProgress.Value = 5;
        StatusText.Text = "正在准备安装…";
        LogExpander.IsExpanded = true;
        _logPath = Path.Combine(_installRoot, "bootstrapper.log");
        _logTimer.Start();

        var progress = new Progress<InstallProgress>(UpdateProgress);
        var result = await _installer.InstallAsync(
            new InstallOptions(_installRoot, _media),
            progress,
            _cancellation.Token);

        _logTimer.Stop();
        RefreshLog();
        CancelButton.Visibility = Visibility.Collapsed;
        InstallButton.IsEnabled = true;

        if (!result.IsSuccess)
        {
            StatusText.Text = result.Error!.Message;
            OpenLogButton.Visibility = _logPath is not null && File.Exists(_logPath) ? Visibility.Visible : Visibility.Collapsed;
            MessageBox.Show(this, result.Error.ToString(), "ECL 安装失败", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        StatusText.Text = "ECL 已安装。程序文件位于所选目录，Minecraft 工作区仍由首次启动时选择。";
        InstallButton.Content = "安装完成";
        LaunchButton.Visibility = Visibility.Visible;
        OpenLogButton.Visibility = File.Exists(_logPath) ? Visibility.Visible : Visibility.Collapsed;
        InstallProgress.IsIndeterminate = false;
        InstallProgress.Value = 100;
    }

    private void UpdateProgress(InstallProgress progress)
    {
        StatusText.Text = progress.Stage switch
        {
            "validating" => "正在校验安装目录…",
            "copying" => "正在复制部署组件…",
            "installing" => "正在安装（签名验证、解包、逐文件校验由 Bootstrapper 执行）…",
            "upgrading" => "检测到现有安装，正在升级…",
            "completed" => "安装完成。",
            "cancelled" => "安装已取消。",
            "failed" => $"安装失败：{progress.Detail}",
            _ => progress.Stage,
        };

        if (progress.Percent is { } percent)
        {
            InstallProgress.IsIndeterminate = false;
            InstallProgress.Value = percent;
        }
        else
        {
            InstallProgress.IsIndeterminate = true;
        }
    }

    private void RefreshLog()
    {
        if (_logPath is null || !File.Exists(_logPath))
        {
            return;
        }

        try
        {
            using var stream = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();
            if (text.Length > 64 * 1024)
            {
                text = text[^65536..];
            }

            LogBox.Text = text;
            LogBox.ScrollToEnd();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 日志读取失败不应影响安装。
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cancellation?.Cancel();
        StatusText.Text = "正在取消…";
    }

    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (_installRoot is null)
        {
            return;
        }

        var bootstrapper = Path.Combine(_installRoot, "ECL.Bootstrapper.exe");
        if (!File.Exists(bootstrapper))
        {
            StatusText.Text = "未找到 ECL.Bootstrapper.exe。";
            return;
        }

        Process.Start(new ProcessStartInfo(bootstrapper)
        {
            UseShellExecute = true,
            WorkingDirectory = _installRoot,
        });
        Close();
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        if (_logPath is null || !File.Exists(_logPath))
        {
            return;
        }

        Process.Start(new ProcessStartInfo(_logPath) { UseShellExecute = true });
    }

    private string PathBoxValue() => InstallPathBox.Text.Trim();

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
}
