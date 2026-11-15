// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.App.Services;

/// <summary>存储占用的统一显示格式；存储中心与实例基本信息页共用，避免两套数字表达。</summary>
public static class StorageSizeFormatter
{
    public static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024 * 1024):0.##} GB",
        >= 1024L * 1024 => $"{bytes / (1024d * 1024):0.##} MB",
        >= 1024 => $"{bytes / 1024d:0.##} KB",
        _ => $"{bytes} B",
    };
}
