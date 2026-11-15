// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace Ecl.Core.Diagnostics;

/// <summary>
/// 读取一次系统内存采样（物理总量/可用量、提交余量）。顶部压力监控与
/// 启动服务的自动内存策略共用同一份采样，避免出现两套“系统内存”口径。
/// </summary>
public interface ISystemMemoryProbe
{
    MemoryPressureSample? Read();
}

public sealed class SystemMemoryProbe : ISystemMemoryProbe
{
    public MemoryPressureSample? Read() => ReadSample();

    public static MemoryPressureSample? ReadSample()
    {
        if (OperatingSystem.IsWindows())
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (GlobalMemoryStatusEx(ref status))
            {
                return new MemoryPressureSample(
                    (long)status.AvailablePhysical,
                    (long)status.TotalPhysical,
                    (long)status.AvailablePageFile,
                    (long)status.TotalPageFile);
            }
        }

        // 非 Windows 平台使用 .NET 的运行时可观察内存；拿不到可用量时返回 null，
        // 调用方必须显示“不可用”，不能把未知值写成 0。
        var info = GC.GetGCMemoryInfo();
        if (info.TotalAvailableMemoryBytes <= 0)
        {
            return null;
        }

        var available = Math.Max(0, info.TotalAvailableMemoryBytes - info.MemoryLoadBytes);
        return new MemoryPressureSample(available, info.TotalAvailableMemoryBytes, 0, 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
