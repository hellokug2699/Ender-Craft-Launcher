// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Sessions;

/// <summary>
/// 采集状态判定。采集“降级”与“故障”必须区分：
/// 降级（独立 LogHost 不可用、改用进程内重定向）仍然产生 stdout/stderr 证据，
/// 只是主 UI 退出后采集会中断；故障（丢包、写入失败、宿主异常退出）才代表证据可能缺失。
/// </summary>
public static class SessionCaptureStatus
{
    private const string LegacyHostFallbackPrefix = "LogHost 不可用";

    /// <summary>
    /// 旧版本在没有独立宿主时会把降级写成 <c>CaptureFault</c>（历史数据）。
    /// 这类记录在展示层按“降级”处理，避免把正常采集显示成故障。
    /// </summary>
    public static bool IsLegacyHostFallback(string? fault) =>
        !string.IsNullOrWhiteSpace(fault) &&
        fault.TrimStart().StartsWith(LegacyHostFallbackPrefix, StringComparison.Ordinal);

    /// <summary>本次会话是否发生采集降级（新记录或历史降级记录）。</summary>
    public static bool IsDegraded(SessionManifest manifest) =>
        !string.IsNullOrWhiteSpace(manifest.CaptureDegraded) ||
        IsLegacyHostFallback(manifest.CaptureFault);

    /// <summary>真正需要提示的采集故障；历史降级记录不算。</summary>
    public static string? FaultOrNull(SessionManifest manifest) =>
        string.IsNullOrWhiteSpace(manifest.CaptureFault) || IsLegacyHostFallback(manifest.CaptureFault)
            ? null
            : manifest.CaptureFault;

    /// <summary>降级原因文本（优先新字段，回退到历史故障记录）。</summary>
    public static string? DegradedReason(SessionManifest manifest) =>
        !string.IsNullOrWhiteSpace(manifest.CaptureDegraded)
            ? manifest.CaptureDegraded
            : IsLegacyHostFallback(manifest.CaptureFault)
                ? manifest.CaptureFault
                : null;
}
