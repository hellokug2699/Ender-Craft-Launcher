// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.Win32;

namespace Ecl.Deployment.Startup;

/// <summary>登录启动项的稳定标识。改动会使用户已存在的启动项失效，因此保持字面值不变。</summary>
public static class RunKeyNames
{
    public const string ValueName = "EnderCraftLauncher";

    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
}

/// <summary>
/// HKCU Run 项的读写抽象。抽象存在的理由是测试必须能在不触碰真实注册表的前提下
/// 覆盖“写入失败 / 被外部删除 / 平台不支持”这些真实会发生的分支。
/// </summary>
public interface IRunKeyStore
{
    /// <summary>读取值；值不存在时返回 true 且 <paramref name="command"/> 为 null。</summary>
    bool TryRead(string valueName, out string? command, out string? error);

    bool TryWrite(string valueName, string command, out string? error);

    /// <summary>删除值；值本来就不存在也算成功。</summary>
    bool TryDelete(string valueName, out string? error);
}

/// <summary>
/// 当前用户的 Windows 登录启动项（<c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>）。
///
/// 只写 HKCU：不需要管理员权限，也不会替其他用户或全机器注册启动项。
/// 注册表异常一律映射为 <c>error</c> 文本返回，不向上抛异常——启动流程与设置页
/// 都不应该因为一条注册表写入失败而崩溃。
/// </summary>
public sealed class HkcuRunKeyStore(string? keyPath = null) : IRunKeyStore
{
    private readonly string _keyPath = string.IsNullOrWhiteSpace(keyPath) ? RunKeyNames.RunKeyPath : keyPath;

    public bool TryRead(string valueName, out string? command, out string? error)
    {
        command = null;
        error = null;
        if (!OperatingSystem.IsWindows())
        {
            error = "当前平台不支持 Windows 登录启动项。";
            return false;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(_keyPath, writable: false);
            command = key?.GetValue(valueName) as string;
            return true;
        }
        catch (Exception exception) when (IsRegistryFailure(exception))
        {
            error = exception.Message;
            return false;
        }
    }

    public bool TryWrite(string valueName, string command, out string? error)
    {
        error = null;
        if (!OperatingSystem.IsWindows())
        {
            error = "当前平台不支持 Windows 登录启动项。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(valueName) || string.IsNullOrWhiteSpace(command))
        {
            error = "启动项名称与命令都不能为空。";
            return false;
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(_keyPath, writable: true);
            if (key is null)
            {
                error = "无法创建或打开登录启动项注册表键。";
                return false;
            }

            key.SetValue(valueName, command, RegistryValueKind.String);
            return true;
        }
        catch (Exception exception) when (IsRegistryFailure(exception))
        {
            error = exception.Message;
            return false;
        }
    }

    public bool TryDelete(string valueName, out string? error)
    {
        error = null;
        if (!OperatingSystem.IsWindows())
        {
            error = "当前平台不支持 Windows 登录启动项。";
            return false;
        }

        try
        {
            // 键不存在时 OpenSubKey 返回 null；值不存在时 DeleteValue 抛 ArgumentException，
            // 两者都属于“本来就没有”，按成功处理，避免卸载/关闭自动启动被误报为失败。
            using var key = Registry.CurrentUser.OpenSubKey(_keyPath, writable: true);
            if (key is null)
            {
                return true;
            }

            try
            {
                key.DeleteValue(valueName, throwOnMissingValue: false);
            }
            catch (ArgumentException)
            {
                return true;
            }

            return true;
        }
        catch (Exception exception) when (IsRegistryFailure(exception))
        {
            error = exception.Message;
            return false;
        }
    }

    private static bool IsRegistryFailure(Exception exception) =>
        exception is System.Security.SecurityException
            or UnauthorizedAccessException
            or IOException
            or ArgumentException
            or ObjectDisposedException;
}
