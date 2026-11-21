// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using Ecl.App.Tests.Support;
using Ecl.App.ViewModels.Security;
using Ecl.Core.Localization;
using Ecl.Minecraft.Instances;
using Ecl.Minecraft.Security;
using Xunit;

namespace Ecl.App.Tests.Security;

/// <summary>
/// 安全中心响应性回归（性能交接 §7、§9.4）：实例枚举与扫描的同步工作
/// 不得在 UI Dispatcher 上执行，扫描期间界面保持可重入。
/// </summary>
public sealed class SecurityScanResponsivenessTests
{
    [Fact]
    public async Task ScanWorkRunsOffTheDispatcherThread()
    {
        using var ui = new UiThread();
        var instances = new FakeInstanceService();
        instances.Instances.Add(Instance("a"));
        instances.Instances.Add(Instance("b"));

        var scanThreads = new List<int>();
        var security = new FakeSecurityService
        {
            Scan = (instanceId, _) =>
            {
                lock (scanThreads)
                {
                    scanThreads.Add(Environment.CurrentManagedThreadId);
                }

                return Task.FromResult(Ecl.Core.Result<ThreatScanReport>.Ok(
                    FakeSecurityService.Report(instanceId)));
            },
        };
        var page = new SecurityCenterViewModel(
            instances,
            security,
            new FakeDialogEngine(),
            new RecordingOperationService(),
            new JsonLocalizer(CultureInfo.GetCultureInfo("zh-CN")));

        await ui.InvokeAsync(() => page.StartScanCommand.ExecuteAsync(null)).Unwrap();

        var dispatcherThread = ui.Dispatcher.Thread.ManagedThreadId;
        Assert.NotEqual(dispatcherThread, instances.GetInstancesThreadId);
        Assert.Equal(2, scanThreads.Count);
        Assert.All(scanThreads, threadId => Assert.NotEqual(dispatcherThread, threadId));
        Assert.Equal(SecurityOverallState.Clean, page.OverallState);
    }

    [Fact]
    public async Task RepeatedStartWhileScanningIsRejected()
    {
        using var ui = new UiThread();
        var instances = new FakeInstanceService();
        instances.Instances.Add(Instance("a"));

        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var security = new FakeSecurityService
        {
            Scan = async (instanceId, cancellationToken) =>
            {
                Interlocked.Increment(ref calls);
                await gate.Task;
                return Ecl.Core.Result<ThreatScanReport>.Ok(FakeSecurityService.Report(instanceId));
            },
        };
        var page = new SecurityCenterViewModel(
            instances,
            security,
            new FakeDialogEngine(),
            new RecordingOperationService(),
            new JsonLocalizer(CultureInfo.GetCultureInfo("zh-CN")));

        var first = ui.InvokeAsync(() => page.StartScanCommand.ExecuteAsync(null));
        await WaitUntilAsync(() => page.IsScanning);
        await ui.InvokeAsync(() => page.StartScanCommand.ExecuteAsync(null)).Unwrap();
        gate.TrySetResult(true);
        await first.Unwrap();

        Assert.Equal(1, calls);
        Assert.False(page.IsScanning);
        Assert.Equal(SecurityOverallState.Clean, page.OverallState);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.True(condition(), "等待条件超时。");
    }

    private static GameInstance Instance(string id) => new()
    {
        Id = id,
        Name = id,
        GameVersion = "1.20.1",
        VersionId = "1.20.1-vanilla",
    };
}
