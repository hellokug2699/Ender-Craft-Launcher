// SPDX-License-Identifier: GPL-3.0-or-later

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

    /// <summary>
    /// 读取一次系统内存采样（总量/可用量）。页面用它展示内存条，
    /// 与压力分级使用同一数据源，避免出现两套“系统内存”口径。
    /// </summary>
    MemoryPressureSample? ReadSample();
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

    public MemoryPressureSample? ReadSample() => TryReadSample();

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

    // 与启动服务的自动内存策略共用同一份系统采样，见 SystemMemoryProbe。
    private static MemoryPressureSample? TryReadSample() => SystemMemoryProbe.ReadSample();

    public void Dispose()
    {
        _timer.Stop();
        LevelChanged = null;
    }
}
