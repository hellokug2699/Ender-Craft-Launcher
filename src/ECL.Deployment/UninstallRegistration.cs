// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;
using Microsoft.Win32;

namespace Ecl.Deployment;

/// <summary>
/// Windows “应用和功能”卸载入口。默认写 HKCU，普通用户权限即可；
/// 卸载字符串直接指向独立 Uninstaller，不经过 Bootstrapper。
/// </summary>
public sealed class UninstallRegistration(string? keyPath = null)
{
    public const string DefaultKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\EnderCraftLauncher";

    private readonly string _keyPath = keyPath ?? DefaultKeyPath;

    public Result Register(string installRoot, string version)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot));
        var uninstaller = Path.Combine(root, InstallManifest.UninstallerFileName);
        if (!File.Exists(uninstaller))
        {
            return Result.Fail("install.uninstaller_missing", $"未找到卸载器：{uninstaller}");
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(_keyPath, writable: true);
            if (key is null)
            {
                return Result.Fail("install.registry_failed", "无法创建卸载注册项。");
            }

            key.SetValue("DisplayName", "Ender Craft Launcher（ECL）", RegistryValueKind.String);
            key.SetValue("DisplayVersion", version, RegistryValueKind.String);
            key.SetValue("Publisher", "Ender Craft Launcher Contributors", RegistryValueKind.String);
            key.SetValue("InstallLocation", root, RegistryValueKind.String);
            key.SetValue("DisplayIcon", Path.Combine(root, "ECL.Bootstrapper.exe"), RegistryValueKind.String);
            key.SetValue(
                "UninstallString",
                $"\"{uninstaller}\" --install-root \"{root}\"",
                RegistryValueKind.String);
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            return Result.Ok();
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return Result.Fail("install.registry_failed", $"写入卸载注册信息失败：{exception.Message}");
        }
    }

    public Result Unregister()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(_keyPath, throwOnMissingSubKey: false);
            return Result.Ok();
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return Result.Fail("uninstall.registry_failed", $"删除卸载注册信息失败：{exception.Message}");
        }
    }

    public bool IsRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_keyPath);
        return key is not null;
    }
}
