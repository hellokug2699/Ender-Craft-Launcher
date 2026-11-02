// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using Ecl.Deployment;

namespace Ecl.Uninstaller;

internal static class Program
{
    private static int Main(string[] args)
    {
        var installRoot = ReadValue(args, "--install-root") ?? AppContext.BaseDirectory;
        var confirmed = args.Any(argument => string.Equals(argument, "--yes", StringComparison.OrdinalIgnoreCase));
        var silent = args.Any(argument => string.Equals(argument, "--silent", StringComparison.OrdinalIgnoreCase));

        Console.WriteLine("Ender Craft Launcher 卸载程序");
        Console.WriteLine($"安装目录：{Path.GetFullPath(installRoot)}");
        Console.WriteLine();
        Console.WriteLine("默认范围：删除 ECL 主程序、Bootstrapper、Uninstaller、快捷方式与卸载注册信息。");
        Console.WriteLine("默认保留：Minecraft 实例、世界存档、模组、资源包、用户配置、账户与日志。");
        Console.WriteLine();

        var manifest = InstallManifestStore.Read(installRoot);
        if (!manifest.IsSuccess)
        {
            Console.Error.WriteLine($"无法卸载：{manifest.Error!.Message}");
            PauseIfInteractive(silent);
            return 3;
        }

        var validation = manifest.Value!;
        if (!validation.IsValid)
        {
            Console.Error.WriteLine("安装清单无效，已停止自动删除：");
            foreach (var error in validation.Errors)
            {
                Console.Error.WriteLine($"  - {error}");
            }

            PauseIfInteractive(silent);
            return 3;
        }

        Console.WriteLine($"程序文件：{validation.Manifest.Files.Count} 个；版本：{validation.Manifest.Version}");
        if (!confirmed && !silent)
        {
            Console.Write("确认卸载 ECL？输入 y 继续：");
            var answer = Console.ReadLine();
            if (!string.Equals(answer?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("已取消，未删除任何文件。");
                return 0;
            }
        }

        if (IsEclRunning())
        {
            Console.Error.WriteLine("检测到 ECL 或 Bootstrapper 正在运行。请先退出启动器与游戏，再重新运行卸载程序。");
            PauseIfInteractive(silent);
            return 4;
        }

        using var updateLock = UpdateLock.TryAcquire(installRoot);
        if (updateLock is null)
        {
            Console.Error.WriteLine("另一个更新或安装任务正在使用该安装目录，请稍后再试。");
            PauseIfInteractive(silent);
            return 4;
        }

        var result = UninstallService.Run(installRoot);
        foreach (var error in result.Errors)
        {
            Console.WriteLine($"  - {error}");
        }

        if (result.Success)
        {
            Console.WriteLine($"卸载完成：删除 {result.DeletedFiles} 个程序文件。用户数据未删除。");
        }
        else
        {
            Console.Error.WriteLine($"卸载部分完成：删除 {result.DeletedFiles} 个，失败 {result.FailedFiles} 个。");
        }

        if (!silent && result.UninstallerPath is not null && File.Exists(result.UninstallerPath))
        {
            ScheduleSelfDelete(result.UninstallerPath, Path.GetFullPath(installRoot));
            Console.WriteLine("卸载器将在退出后自行清理。");
        }

        PauseIfInteractive(silent);
        return result.Success ? 0 : 5;
    }

    private static bool IsEclRunning()
    {
        foreach (var name in new[] { "ECL", "ECL.Bootstrapper" })
        {
            try
            {
                if (Process.GetProcessesByName(name).Length > 0)
                {
                    return true;
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // 无法枚举进程时保守地继续；文件占用会在删除阶段报告。
            }
        }

        return false;
    }

    private static void ScheduleSelfDelete(string executable, string installRoot)
    {
        try
        {
            var command = $"ping 127.0.0.1 -n 3 >nul & del /f /q \"{executable}\" & rmdir \"{installRoot}\"";
            Process.Start(new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { "/d", "/c", command },
            });
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Console.WriteLine("无法安排卸载器自清理；可手动删除：" + executable);
        }
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

    private static void PauseIfInteractive(bool silent)
    {
        if (!silent && Environment.UserInteractive)
        {
            Console.WriteLine();
            Console.WriteLine("按 Enter 退出。");
            Console.ReadLine();
        }
    }
}
