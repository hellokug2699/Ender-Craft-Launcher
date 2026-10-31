// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using Ecl.Cli;
using Ecl.Core.Localization;
using Ecl.Operations;
using Xunit;

namespace Ecl.Tests.Cli;

public sealed class CliOperationReporterTests
{
    [Fact]
    public void PrintsStageProgressRetryAndFailureAttribution()
    {
        using var service = new OperationService();
        using var writer = new StringWriter();
        using var reporter = new CliOperationReporter(
            service,
            new JsonLocalizer(CultureInfo.GetCultureInfo("zh-CN")),
            writer);

        using var bridge = new OperationProgressBridge<int>(
            service,
            new OperationStart
            {
                Kind = OperationKind.Install,
                Title = "安装 Test",
                StageId = "install",
                TotalBytes = 100,
                CanCancel = true,
            },
            null,
            value => new OperationProgressUpdate
            {
                StageId = "install.libraries",
                StageText = "下载依赖库…",
                Completed = value,
                TotalBytes = 100,
                BytesDownloaded = value,
            });

        bridge.Report(0);
        bridge.Report(50);
        bridge.Handle!.Retrying(2, 3, "HTTP 503");
        bridge.Fail(new OperationError(OperationErrorKind.Network, "install.download_failed", "连接中断"));

        var text = writer.ToString();
        Assert.Contains("下载依赖库…", text, StringComparison.Ordinal);
        Assert.Contains("0% · 0 B / 100 B", text, StringComparison.Ordinal);

        // 进度按 1 秒节流：同一秒内的后续进度不再刷屏。
        Assert.DoesNotContain("50%", text, StringComparison.Ordinal);

        Assert.Contains("正在重试（2/3）：HTTP 503", text, StringComparison.Ordinal);
        Assert.Contains("失败原因：网络（连接中断）", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TracksOnlyTheFirstRunningOperation()
    {
        using var service = new OperationService();
        using var writer = new StringWriter();
        using var reporter = new CliOperationReporter(
            service,
            new JsonLocalizer(CultureInfo.GetCultureInfo("zh-CN")),
            writer);

        using var first = new OperationProgressBridge<int>(
            service,
            new OperationStart { Kind = OperationKind.Install, Title = "first" },
            null,
            value => new OperationProgressUpdate { StageText = $"第一层 {value}" });
        first.Report(1);

        using var second = new OperationProgressBridge<int>(
            service,
            new OperationStart { Kind = OperationKind.Download, Title = "second" },
            null,
            value => new OperationProgressUpdate { StageText = $"第二层 {value}" });
        second.Report(1);

        var text = writer.ToString();
        Assert.Contains("第一层 1", text, StringComparison.Ordinal);
        Assert.DoesNotContain("第二层", text, StringComparison.Ordinal);
    }
}
