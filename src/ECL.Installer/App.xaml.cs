// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO;
using System.Windows;
using Ecl.Deployment;

namespace Ecl.Installer;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (!e.Args.Any(argument => string.Equals(argument, "--silent", StringComparison.OrdinalIgnoreCase)))
        {
            new MainWindow().Show();
            return;
        }

        Shutdown(await RunSilentAsync(e.Args));
    }

    private static async Task<int> RunSilentAsync(string[] args)
    {
        var installRoot = ReadValue(args, "--install-root");
        var mediaDirectory = ReadValue(args, "--media") ?? AppContext.BaseDirectory;
        var package = ReadValue(args, "--package");

        if (string.IsNullOrWhiteSpace(installRoot) || !Path.IsPathFullyQualified(installRoot))
        {
            Console.Error.WriteLine("--silent 需要绝对路径的 --install-root <目录>。");
            return 2;
        }

        // ADR 0025：静默安装不得把“未验证”伪装成已确认。自动化必须显式承担风险。
        if (!args.Any(argument => string.Equals(argument, "--accept-unverified", StringComparison.OrdinalIgnoreCase)))
        {
            Console.Error.WriteLine(
                "静默安装未执行首次完整性核对。若确认已通过官方 Release 核对，请显式传入 --accept-unverified。");
            return 2;
        }

        var media = InstallMedia.Inspect(mediaDirectory, package);
        if (!media.IsSuccess)
        {
            WriteLog(installRoot, media.Error!.ToString());
            return 2;
        }

        var installer = new InstallerService();
        var result = await installer.InstallAsync(new InstallOptions(installRoot, media.Value!));
        if (!result.IsSuccess)
        {
            WriteLog(installRoot, result.Error!.ToString());
            return 4;
        }

        WriteLog(installRoot, $"安装完成，退出码 {result.Value!.ExitCode}。");
        return 0;
    }

    private static string? ReadValue(string[] args, string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private static void WriteLog(string installRoot, string message)
    {
        try
        {
            Directory.CreateDirectory(installRoot);
            File.AppendAllText(
                Path.Combine(installRoot, "installer-silent.log"),
                $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 无法写日志时仍以退出码表达结果。
        }
    }
}
