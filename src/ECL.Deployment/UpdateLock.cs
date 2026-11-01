// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Deployment;

/// <summary>
/// 安装根目录级部署锁（ADR 0016 §4）。使用独占文件句柄，进程异常退出时由操作系统释放，
/// 不会形成永久不可解除的逻辑锁。
/// </summary>
public static class UpdateLock
{
    public const string FileName = ".update.lock";

    public static FileStream? TryAcquire(string installRoot)
    {
        try
        {
            Directory.CreateDirectory(installRoot);
            return new FileStream(
                Path.Combine(installRoot, FileName),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
