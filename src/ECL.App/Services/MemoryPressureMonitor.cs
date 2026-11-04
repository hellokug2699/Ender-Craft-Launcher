// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using System.Windows.Threading;
using Ecl.Core.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Ecl.App.Services;

public interface IMemoryPressureMonitor
{
    MemoryPressureLevel Level { get; }

    event EventHandler<MemoryPressureLevel>? LevelChanged;

    void Start();

    void Stop();
}

/// <summary>
/// 周期性采样系统可用内存与提交余量，并驱动分级降级。采样本身保持轻量：
/// 只在 UI 线程做一次算术，不分配大对象、不扫描进程。
/// </summary>
public sealed class MemoryPressureMonitor : IMemoryPressureMonitor, IDisposable
{
    private readonly MemoryPressureStateMachine _machine = new();
    private readonly DispatcherTimer _timer;
    private readonly ILogger<MemoryPressureMonitor> _logger;

    public MemoryPressureMonitor(ILogger<MemoryPressureMonitor> logger)
    {
        _logger = logger;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => Sample();
    }

    public MemoryPressureLevel Level => _machine.Level;

    public event EventHandler<MemoryPressureLevel>? LevelChanged;

    public void Start() => _timer.Start();

    public void Stop() => _timer.Stop();

    private void Sample()
    {
        var sample = TryReadSample();
        if (sample is null)
        {
            return;
        }

        var previous = _machine.Level;
        var level = _machine.Observe(DateTimeOffset.UtcNow, sample);
        if (level == previous)
        {
            return;
        }

        _logger.LogInformation("内存压力等级：{Previous} → {Level}", previous, level);
        LevelChanged?.Invoke(this, level);
    }

    private static MemoryPressureSample? TryReadSample()
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

        var info = GC.GetGCMemoryInfo();
        if (info.TotalAvailableMemoryBytes <= 0)
        {
            return null;
        }

        var available = Math.Max(0, info.TotalAvailableMemoryBytes - info.MemoryLoadBytes);
        return new MemoryPressureSample(available, info.TotalAvailableMemoryBytes, 0, 0);
    }

    public void Dispose()
    {
        _timer.Stop();
        LevelChanged = null;
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
