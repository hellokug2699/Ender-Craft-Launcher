// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Operations;

/// <summary>任务中心与 CLI 共用的数值格式，避免两处显示不一致。</summary>
public static class OperationFormat
{
    public static string Bytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024d / 1024 / 1024:F2} GB",
        >= 1024L * 1024 => $"{bytes / 1024d / 1024:F1} MB",
        >= 1024 => $"{bytes / 1024d:F0} KB",
        > 0 => $"{bytes} B",
        _ => "0 B",
    };

    public static string Speed(double bytesPerSecond) => bytesPerSecond switch
    {
        >= 1024 * 1024 => $"{bytesPerSecond / 1024 / 1024:F1} MB/s",
        >= 1024 => $"{bytesPerSecond / 1024:F0} KB/s",
        > 0 => $"{bytesPerSecond:F0} B/s",
        _ => string.Empty,
    };
}
