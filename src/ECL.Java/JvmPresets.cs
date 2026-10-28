// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Java;

public enum JvmPreset
{
    Default,
    G1,
    Zgc,
    Aikar,
}

/// <summary>
/// 启动器内置的 JVM 参数预设。预设只包含 GC/堆行为参数，不包含 -Xms/-Xmx，
/// 内存仍由实例设置统一控制。
/// </summary>
public static class JvmPresets
{
    public static IReadOnlyList<string> Ids { get; } = ["default", "g1", "zgc", "aikar"];

    public static bool TryParse(string? value, out JvmPreset preset)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case null or "" or "default" or "none":
                preset = JvmPreset.Default;
                return true;
            case "g1":
                preset = JvmPreset.G1;
                return true;
            case "zgc":
                preset = JvmPreset.Zgc;
                return true;
            case "aikar":
                preset = JvmPreset.Aikar;
                return true;
            default:
                preset = JvmPreset.Default;
                return false;
        }
    }

    public static JvmPreset Parse(string? value) =>
        TryParse(value, out var preset) ? preset : JvmPreset.Default;

    public static string ToId(JvmPreset preset) => preset switch
    {
        JvmPreset.G1 => "g1",
        JvmPreset.Zgc => "zgc",
        JvmPreset.Aikar => "aikar",
        _ => "default",
    };

    public static IReadOnlyList<string> GetArguments(JvmPreset preset, JavaVersion? javaVersion)
    {
        var major = javaVersion?.Major ?? 17;
        return preset switch
        {
            JvmPreset.G1 => G1Flags,
            JvmPreset.Aikar => AikarFlags,
            JvmPreset.Zgc => GetZgcFlags(major),
            _ => [],
        };
    }

    private static IReadOnlyList<string> GetZgcFlags(int javaMajor)
    {
        if (javaMajor < 17)
        {
            // ZGC 从 Java 15 开始可用，但对 Minecraft 来说 17 以下没有实际意义；
            // 不注入不存在的参数，交给兼容性提示处理。
            return [];
        }

        return javaMajor is >= 21 and < 24
            ? ["-XX:+UseZGC", "-XX:+ZGenerational", "-XX:+AlwaysPreTouch", "-XX:+DisableExplicitGC"]
            : ["-XX:+UseZGC", "-XX:+AlwaysPreTouch", "-XX:+DisableExplicitGC"];
    }

    private static readonly string[] G1Flags =
    [
        "-XX:+UseG1GC",
        "-XX:+ParallelRefProcEnabled",
        "-XX:MaxGCPauseMillis=200",
        "-XX:+UnlockExperimentalVMOptions",
        "-XX:+DisableExplicitGC",
        "-XX:+AlwaysPreTouch",
    ];

    // Aikar's flags（G1 + 大堆行为调优），不含 -Xms/-Xmx。
    private static readonly string[] AikarFlags =
    [
        "-XX:+UseG1GC",
        "-XX:+ParallelRefProcEnabled",
        "-XX:MaxGCPauseMillis=200",
        "-XX:+UnlockExperimentalVMOptions",
        "-XX:+DisableExplicitGC",
        "-XX:+AlwaysPreTouch",
        "-XX:G1NewSizePercent=30",
        "-XX:G1MaxNewSizePercent=40",
        "-XX:G1HeapRegionSize=8M",
        "-XX:G1ReservePercent=20",
        "-XX:G1HeapWastePercent=5",
        "-XX:G1MixedGCCountTarget=4",
        "-XX:InitiatingHeapOccupancyPercent=15",
        "-XX:G1MixedGCLiveThresholdPercent=90",
        "-XX:G1RSetUpdatingPauseTimePercent=5",
        "-XX:SurvivorRatio=32",
        "-XX:+PerfDisableSharedMem",
        "-XX:MaxTenuringThreshold=1",
    ];
}
