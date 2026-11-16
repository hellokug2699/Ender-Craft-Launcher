// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Ecl.Core.Storage;

/// <summary>
/// 平台文件对象身份（F-02 §4.3/§8.5）。优先使用卷序列号 + 文件 ID，用于识别
/// junction/别名/盘符变化后仍指向同一物理目录的情况；不可用时返回 <c>null</c>，
/// 调用方必须退回规范化路径比较并明确标记“潜在重复”，不得猜测。
/// </summary>
public static class PathIdentity
{
    public const int KeyVersion = 1;

    private const uint FileReadAttributes = 0x0080;
    private const uint FileShareRead = 0x0000_0001;
    private const uint FileShareWrite = 0x0000_0002;
    private const uint FileShareDelete = 0x0000_0004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x0200_0000;
    private const uint VolumeNameDos = 0x0000_0000;

    /// <summary>
    /// 解析路径的最终（重解析点展开后）路径。失败时返回 <c>null</c>，调用方保留原规范化路径。
    /// 只读操作：不创建句柄之外的任何副作用。
    /// </summary>
    public static string? TryGetFinalPath(string path)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        SafeFileHandle? handle = null;
        try
        {
            handle = CreateFileW(
                path,
                FileReadAttributes,
                FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                return null;
            }

            var buffer = new StringBuilder(1024);
            var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, VolumeNameDos);
            if (length == 0 || length >= buffer.Capacity)
            {
                return null;
            }

            return Normalize(buffer.ToString());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            handle?.Dispose();
        }
    }

    /// <summary>
    /// 读取卷序列号 + 文件 ID。目录与文件均可；路径不存在或平台不支持时返回 <c>null</c>。
    /// 返回格式 <c>volume:fileId</c>，稳定且不包含用户可见路径。
    /// </summary>
    public static string? TryGetKey(string path)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        SafeFileHandle? handle = null;
        try
        {
            handle = CreateFileW(
                path,
                FileReadAttributes,
                FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics,
                IntPtr.Zero);
            if (handle.IsInvalid ||
                !GetFileInformationByHandle(handle, out var information))
            {
                return null;
            }

            return string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{information.VolumeSerialNumber:x8}:{information.FileIndexHigh:x8}{information.FileIndexLow:x8}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            handle?.Dispose();
        }
    }

    /// <summary>规范化路径：绝对路径、去尾分隔符。失败时返回 <c>null</c>。</summary>
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>判断 <paramref name="path"/> 是否等于或位于 <paramref name="root"/> 之内。</summary>
    public static bool IsUnder(string? path, string? root)
    {
        var normalizedPath = Normalize(path);
        var normalizedRoot = Normalize(root);
        if (normalizedPath is null || normalizedRoot is null)
        {
            return false;
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(normalizedPath, normalizedRoot, comparison) ||
               normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison);
    }

    /// <summary>比较两个路径是否指向同一位置（大小写按平台规则）。</summary>
    public static bool PathsEqual(string? left, string? right)
    {
        var normalizedLeft = Normalize(left);
        var normalizedRight = Normalize(right);
        if (normalizedLeft is null || normalizedRight is null)
        {
            return false;
        }

        return string.Equals(
            normalizedLeft,
            normalizedRight,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static SafeFileHandle CreateFileW(
        string path,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile) =>
        CreateFileInternal(
            path,
            desiredAccess,
            shareMode,
            securityAttributes,
            creationDisposition,
            flagsAndAttributes,
            templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFileInternal(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation fileInformation);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle file,
        StringBuilder filePath,
        uint filePathLength,
        uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
