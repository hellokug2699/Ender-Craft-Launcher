// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Configuration.Settings;
using Ecl.Core.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Ecl.Minecraft.Launch;

public enum MemoryAllocationMode
{
    Automatic,
    Manual,
}

/// <summary>
/// 一次内存分配解析结果。字段全部来自真实环境或配置，不把未知值写成 0：
/// 系统内存不可用时 <see cref="IsEstimateReliable"/> 为 false，并采用保守回退值。
/// </summary>
public sealed record MemoryAllocationPlan(
    MemoryAllocationMode Mode,
    int EffectiveMb,
    long TotalPhysicalMb,
    long AvailablePhysicalMb,
    bool IsEstimateReliable);

/// <summary>
/// 全局内存策略（settings-game-launch.md §4.1，D-03）：
///
/// - <c>java.memoryPolicy</c> 是新策略键，默认 <c>auto</c>；
/// - <c>java.defaultMemoryMb</c> 继续作为手工数值保存，数值键不被改写；
/// - 实例 <c>instance.java.memoryMb</c> 永远优先于全局策略；
/// - 旧配置兼容：策略键缺失但手工数值已显式保存时按“手动指定”处理，
///   保证迁移未运行或迁移失败时不会把用户明确设置的数值静默改成自动。
///
/// 自动算法只使用当前可用物理内存，不按物理内存总量的固定比例分配，也不假设机器
/// 始终拥有相同的可用内存。游戏版本没有可验证的“版本 → 内存”规则，因此不参与计算。
/// </summary>
public static class MemoryAllocationPolicy
{
    public const string PolicyKey = "java.memoryPolicy";

    public const string ManualValueKey = "java.defaultMemoryMb";

    public const string Automatic = "auto";

    public const string Manual = "manual";

    /// <summary>系统内存无法读取时的保守回退值（旧默认值）。</summary>
    public const int FallbackMemoryMb = 4096;

    public const int MinimumMemoryMb = 1024;

    public const int MaximumMemoryMb = 65536;

    /// <summary>
    /// 按当前可用物理内存选择自动分配值。分档而不是固定比例：可用内存越低，
    /// 为操作系统、ECL 与其他进程保留的空间占比越高。
    /// 返回 <c>null</c> 表示可用内存未知，调用方不得把它当成 0。
    /// </summary>
    public static int? ComputeAutomaticMb(long availablePhysicalMb)
    {
        if (availablePhysicalMb <= 0)
        {
            return null;
        }

        return availablePhysicalMb switch
        {
            < 1536 => 1024,
            < 3072 => 1536,
            < 5120 => 2048,
            < 8192 => 3072,
            < 12288 => 4096,
            < 16384 => 6144,
            < 24576 => 8192,
            < 32768 => 12288,
            < 49152 => 16384,
            _ => 24576,
        };
    }
}

public interface IMemoryAllocationService
{
    /// <summary>
    /// 解析全局策略（<paramref name="instanceMemoryMb"/> 为空）或实例显式内存。
    /// 只读取配置与一次系统采样，不写配置、不扫描实例。
    /// </summary>
    MemoryAllocationPlan Resolve(int? instanceMemoryMb = null);
}

public sealed class MemoryAllocationService(ISettingsService settings, ISystemMemoryProbe probe)
    : IMemoryAllocationService
{
    public MemoryAllocationPlan Resolve(int? instanceMemoryMb = null)
    {
        var sample = probe.Read();
        var totalMb = sample is null ? 0 : sample.TotalPhysicalBytes / 1024 / 1024;
        var availableMb = sample is null ? 0 : sample.AvailablePhysicalBytes / 1024 / 1024;

        if (instanceMemoryMb is int instanceValue)
        {
            return new MemoryAllocationPlan(
                MemoryAllocationMode.Manual,
                Math.Clamp(instanceValue, MemoryAllocationPolicy.MinimumMemoryMb, MemoryAllocationPolicy.MaximumMemoryMb),
                totalMb,
                availableMb,
                IsEstimateReliable: true);
        }

        if (ResolveMode(settings) == MemoryAllocationMode.Manual)
        {
            var manual = settings.Get<int>(MemoryAllocationPolicy.ManualValueKey);
            return new MemoryAllocationPlan(
                MemoryAllocationMode.Manual,
                Math.Clamp(manual, MemoryAllocationPolicy.MinimumMemoryMb, MemoryAllocationPolicy.MaximumMemoryMb),
                totalMb,
                availableMb,
                IsEstimateReliable: true);
        }

        var automatic = MemoryAllocationPolicy.ComputeAutomaticMb(availableMb);
        return automatic is null
            ? new MemoryAllocationPlan(
                MemoryAllocationMode.Automatic,
                MemoryAllocationPolicy.FallbackMemoryMb,
                TotalPhysicalMb: 0,
                AvailablePhysicalMb: 0,
                IsEstimateReliable: false)
            : new MemoryAllocationPlan(
                MemoryAllocationMode.Automatic,
                automatic.Value,
                totalMb,
                availableMb,
                IsEstimateReliable: true);
    }

    private static MemoryAllocationMode ResolveMode(ISettingsService settings)
    {
        // 策略键一旦显式保存就以它为准；否则回落到旧配置兼容判断。
        if (settings.GetSource(MemoryAllocationPolicy.PolicyKey) != SettingsValueSource.Default)
        {
            return string.Equals(
                settings.Get<string>(MemoryAllocationPolicy.PolicyKey),
                MemoryAllocationPolicy.Manual,
                StringComparison.OrdinalIgnoreCase)
                ? MemoryAllocationMode.Manual
                : MemoryAllocationMode.Automatic;
        }

        return settings.GetSource(MemoryAllocationPolicy.ManualValueKey) == SettingsValueSource.Global
            ? MemoryAllocationMode.Manual
            : MemoryAllocationMode.Automatic;
    }
}

/// <summary>
/// D-03 的一次性迁移：升级前从未修改过内存值的用户继续落在默认“自动分配”；
/// 明确保存过 <c>java.defaultMemoryMb</c>（包括显式 4096）的用户写入 <c>manual</c>，
/// 数值原样保留。只按可信的配置来源区分，不按数值猜测用户意图。
///
/// 迁移可重复执行：策略键已存在时直接返回，不重写、不改值。
/// </summary>
public static class MemoryPolicyMigration
{
    /// <summary>执行迁移；返回是否写入了一次策略值。失败只记录日志，不阻断启动。</summary>
    public static bool TryMigrate(ISettingsService settings, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        try
        {
            if (settings.GetSource(MemoryAllocationPolicy.PolicyKey) != SettingsValueSource.Default)
            {
                return false;
            }

            if (settings.GetSource(MemoryAllocationPolicy.ManualValueKey) != SettingsValueSource.Global)
            {
                // 没有显式数值：默认策略就是自动，不需要写入。
                return false;
            }

            if (!settings.TrySetRaw(MemoryAllocationPolicy.PolicyKey, MemoryAllocationPolicy.Manual, null, out var error))
            {
                logger?.LogWarning("内存策略迁移未能写入 java.memoryPolicy：{Error}", error);
                return false;
            }

            logger?.LogInformation(
                "内存策略迁移：检测到显式保存的 java.defaultMemoryMb，已将策略标记为手动指定。");
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 迁移失败不能阻断启动；运行时的兼容回退仍会把显式数值当作手动值。
            logger?.LogWarning(exception, "内存策略迁移失败；保留原有内存配置。");
            return false;
        }
    }
}
