// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Sessions;
using Xunit;

namespace Ecl.Tests.Sessions;

/// <summary>
/// 采集“降级”与“故障”的展示判定：LogHost 不可用改用进程内重定向时采集仍正常，
/// 不能显示成“采集存在故障”；只有丢包/写入失败/宿主异常退出才算故障。
/// </summary>
public sealed class SessionCaptureStatusTests
{
    [Fact]
    public void DegradedManifestDoesNotReportFault()
    {
        var manifest = new SessionManifest
        {
            SessionId = "s",
            CaptureDegraded = "未找到 ECL.LogHost.exe",
        };

        Assert.True(SessionCaptureStatus.IsDegraded(manifest));
        Assert.Null(SessionCaptureStatus.FaultOrNull(manifest));
        Assert.Equal("未找到 ECL.LogHost.exe", SessionCaptureStatus.DegradedReason(manifest));
    }

    [Fact]
    public void LegacyFallbackFaultIsTreatedAsDegraded()
    {
        // 旧版本把降级写成 CaptureFault；历史会话仍应显示“降级”，不是“故障”。
        var manifest = new SessionManifest
        {
            SessionId = "s",
            CaptureFault = "LogHost 不可用，已降级为进程内采集：未找到 ECL.LogHost.exe",
        };

        Assert.True(SessionCaptureStatus.IsDegraded(manifest));
        Assert.Null(SessionCaptureStatus.FaultOrNull(manifest));
    }

    [Fact]
    public void RealFaultsStayFaults()
    {
        var manifest = new SessionManifest
        {
            SessionId = "s",
            CaptureFault = "采集队列饱和，丢弃 4096 字节。",
        };

        Assert.False(SessionCaptureStatus.IsDegraded(manifest));
        Assert.Equal("采集队列饱和，丢弃 4096 字节。", SessionCaptureStatus.FaultOrNull(manifest));
    }

    [Fact]
    public void CleanManifestHasNeitherFaultNorDegradation()
    {
        var manifest = new SessionManifest { SessionId = "s" };

        Assert.False(SessionCaptureStatus.IsDegraded(manifest));
        Assert.Null(SessionCaptureStatus.FaultOrNull(manifest));
        Assert.Null(SessionCaptureStatus.DegradedReason(manifest));
    }
}
