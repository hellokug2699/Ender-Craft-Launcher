// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO;

using Ecl.App.Services;
using Ecl.App.Tests.Support;
using Ecl.App.ViewModels.Home;
using Ecl.Minecraft.Launch;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ecl.App.Tests.Home;

/// <summary>
/// 主页启动卡的两条硬约束（home.md §3.2）：估量进度不到 100% 且不显示百分比；
/// 只有“耗时较长 + 一段时间没有新输出”才提示可能卡住，单纯耗时长不算卡死。
/// </summary>
public sealed class HomeLaunchPresentationTests
{
    [Fact]
    public void RunningFractionStartsAtZeroAndNeverReachesCompletion()
    {
        Assert.Equal(0, HomeLaunchPresentation.RunningFraction("files", TimeSpan.Zero));
        var capped = HomeLaunchPresentation.RunningFraction("files", TimeSpan.FromHours(1));
        Assert.InRange(capped, 0, HomeLaunchPresentation.RunningCap);
        Assert.True(capped <= HomeLaunchPresentation.RunningCap);
    }

    [Fact]
    public void RunningFractionIsMonotonic()
    {
        var previous = 0.0;
        for (var seconds = 0; seconds <= 120; seconds += 5)
        {
            var current = HomeLaunchPresentation.RunningFraction("files", TimeSpan.FromSeconds(seconds));
            Assert.True(current >= previous, $"{seconds}s 的估量进度回退了。");
            previous = current;
        }
    }

    [Fact]
    public void TotalProgressCombinesCompletedStepsAndRunningEstimate()
    {
        Assert.Equal(0, HomeLaunchPresentation.TotalProgress(0, 0, 0));
        Assert.Equal(0.25, HomeLaunchPresentation.TotalProgress(4, 1, 0));
        Assert.Equal(0.375, HomeLaunchPresentation.TotalProgress(4, 1, 0.5), 3);
        Assert.Equal(1, HomeLaunchPresentation.TotalProgress(4, 4, 0));
        // 越界输入被夹紧，不会显示超过 100% 的进度。
        Assert.Equal(1, HomeLaunchPresentation.TotalProgress(2, 5, 1));
        Assert.Equal(0, HomeLaunchPresentation.TotalProgress(2, -1, -1));
    }

    [Fact]
    public void LongRunningNoticeRequiresBothElapsedTimeAndOutputSilence()
    {
        var longEnough = HomeLaunchPresentation.LongRunningAfter + TimeSpan.FromSeconds(1);
        var silentEnough = HomeLaunchPresentation.SilentOutputAfter + TimeSpan.FromSeconds(1);

        Assert.True(HomeLaunchPresentation.ShouldReportLongRunning(longEnough, silentEnough));
        // 只是耗时长但仍有输出：不得提示可能卡住。
        Assert.False(HomeLaunchPresentation.ShouldReportLongRunning(longEnough, TimeSpan.FromSeconds(1)));
        // 输出很安静但步骤刚开始：不得提前断言。
        Assert.False(HomeLaunchPresentation.ShouldReportLongRunning(TimeSpan.FromSeconds(1), silentEnough));
    }

    [Fact]
    public void StepRowWalksPendingRunningAndCompletedStates()
    {
        var row = new HomeLaunchStepViewModel("files", HomeTestData.Localizer);

        Assert.Equal(HomeLaunchStepState.Pending, row.State);
        Assert.Equal(HomeTestData.Localizer.Get("launch.step.files"), row.Title);

        row.MarkRunning();
        Assert.True(row.IsRunning);
        row.UpdateElapsed(TimeSpan.Zero);
        Assert.Equal(0, row.ProgressValue);
        row.UpdateElapsed(TimeSpan.FromSeconds(10));
        Assert.InRange(row.ProgressValue, 1, HomeLaunchPresentation.RunningCap * 100);

        row.Complete(LaunchStepStatus.Success, "ok", TimeSpan.FromSeconds(1.5));
        Assert.True(row.IsSuccess);
        Assert.False(row.IsRunning);
        Assert.Equal(100, row.ProgressValue);
        Assert.Equal("ok", row.Message);
        Assert.Equal("1.5s", row.DurationText);
    }

    [Fact]
    public void FailedStepKeepsFailureStateAndNeverCountsAsSuccess()
    {
        var row = new HomeLaunchStepViewModel("files", HomeTestData.Localizer);

        row.Complete(LaunchStepStatus.Failed, "缺少游戏文件", TimeSpan.FromSeconds(2));

        Assert.True(row.IsFailed);
        Assert.False(row.IsSuccess);
        Assert.Equal(100, row.ProgressValue);
        Assert.Equal(HomeTestData.Localizer.Get("launch.status.failed"), row.StateText);
    }

    [Fact]
    public void RestoreUsesPersistedStatusWithoutRunningState()
    {
        var row = new HomeLaunchStepViewModel("files", HomeTestData.Localizer);

        row.Restore(new LaunchStatusStep
        {
            StepId = "files",
            Status = LaunchStepStatus.Success,
            Message = "已检查",
            Duration = TimeSpan.FromSeconds(3),
        });

        Assert.True(row.IsSuccess);
        Assert.False(row.IsRunning);
        Assert.Equal(100, row.ProgressValue);
        Assert.Equal("已检查", row.Message);
    }

    [Fact]
    public void NotRunStepStaysVisuallyEmpty()
    {
        var row = new HomeLaunchStepViewModel("start", HomeTestData.Localizer);

        row.MarkNotRun();

        Assert.Equal(HomeLaunchStepState.NotRun, row.State);
        Assert.Equal(0, row.ProgressValue);
        Assert.Equal(HomeTestData.Localizer.Get("home.launch.state.notRun"), row.StateText);
    }

    [Fact]
    public void LongRunningNoteStatesFactsWithoutClaimingAHang()
    {
        var row = new HomeLaunchStepViewModel("files", HomeTestData.Localizer);
        row.MarkRunning();

        row.SetLongRunningNote(TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(20));

        Assert.Contains("45", row.Note, StringComparison.Ordinal);
        Assert.Contains("20", row.Note, StringComparison.Ordinal);
        Assert.Contains("不等于失败", row.Note, StringComparison.Ordinal);
    }
}

/// <summary>排列只有合法实例 ID 与空位两种值；文件损坏时回退到实例列表顺序。</summary>
public sealed class HomeLayoutStoreTests
{
    [Fact]
    public void SaveAndLoadRoundTripKeepsEmptySlots()
    {
        using var paths = new TemporaryAppPaths();
        var store = new HomeLayoutStore(paths.Paths, NullLogger<HomeLayoutStore>.Instance);

        store.Save(["a", null, "b", null]);

        Assert.Equal(["a", null, "b", null], store.Load());
    }

    [Fact]
    public void MissingFileLoadsAsEmptyLayout()
    {
        using var paths = new TemporaryAppPaths();
        var store = new HomeLayoutStore(paths.Paths, NullLogger<HomeLayoutStore>.Instance);

        Assert.Empty(store.Load());
    }

    [Fact]
    public void CorruptFileFallsBackToEmptyLayout()
    {
        using var paths = new TemporaryAppPaths();
        Directory.CreateDirectory(paths.Paths.ConfigDirectory);
        File.WriteAllText(Path.Combine(paths.Paths.ConfigDirectory, "home-layout.json"), "{ not json");
        var store = new HomeLayoutStore(paths.Paths, NullLogger<HomeLayoutStore>.Instance);

        Assert.Empty(store.Load());
    }

    [Fact]
    public void UnsafeInstanceIdsAreNormalizedToEmptySlots()
    {
        using var paths = new TemporaryAppPaths();
        var store = new HomeLayoutStore(paths.Paths, NullLogger<HomeLayoutStore>.Instance);

        store.Save(["../escape", "a", string.Empty]);

        Assert.Equal([null, "a", null], store.Load());
    }

    private sealed class TemporaryAppPaths : IDisposable
    {
        public TemporaryAppPaths()
        {
            Root = Path.Combine(Path.GetTempPath(), "ecl-home-layout-tests", Guid.NewGuid().ToString("N"));
            Paths = new Ecl.Core.AppPaths(Root);
            Paths.EnsureCreated();
        }

        public string Root { get; }

        public Ecl.Core.AppPaths Paths { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // 临时目录清理失败不影响测试结果。
            }
        }
    }
}


