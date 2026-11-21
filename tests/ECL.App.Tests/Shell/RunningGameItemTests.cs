// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using Ecl.App.Shell.RunningGames;
using Ecl.Core.Localization;
using Ecl.Sessions;
using Xunit;

namespace Ecl.App.Tests.Shell;

/// <summary>
/// 侧边栏运行中实例条目：实例名使用用户设定名、运行时间由进程启动时间推导、
/// 重启后恢复的进程有明确标注（显式需求）。
/// </summary>
public sealed class RunningGameItemTests
{
    [Fact]
    public void ElapsedTimeIsDerivedFromProcessStart()
    {
        var started = new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
        var item = new RunningGameItemViewModel(Snapshot(started), "我的整合包", new StubLocalizer());

        item.UpdateElapsed(started.AddSeconds(65));

        Assert.Equal("01:05", item.ElapsedText);
        Assert.Equal("我的整合包", item.InstanceName);
        Assert.Equal("本次启动", item.OriginText);
        Assert.Equal("PID 4242", item.PidText);
    }

    [Fact]
    public void LongRunningInstanceShowsHours()
    {
        var started = new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
        var item = new RunningGameItemViewModel(Snapshot(started), "实例", new StubLocalizer());

        item.UpdateElapsed(started.Add(new TimeSpan(2, 3, 4)));

        Assert.Equal("2:03:04", item.ElapsedText);
    }

    [Fact]
    public void RecoveredProcessIsMarkedAsStartedBeforeRestart()
    {
        var item = new RunningGameItemViewModel(
            Snapshot(DateTimeOffset.UtcNow, recovered: true),
            "实例",
            new StubLocalizer());

        Assert.Equal("ECL 重启前已运行", item.OriginText);
    }

    [Fact]
    public void ElapsedNeverGoesNegativeForClockSkew()
    {
        var started = DateTimeOffset.UtcNow;
        var item = new RunningGameItemViewModel(Snapshot(started), "实例", new StubLocalizer());

        item.UpdateElapsed(started.AddSeconds(-30));

        Assert.Equal("00:00", item.ElapsedText);
    }

    private static RunningGameSnapshot Snapshot(DateTimeOffset startedAt, bool recovered = false) => new(
        "20261121-120000-abcd",
        "instance-1",
        "session-name",
        4242,
        startedAt,
        recovered);

    private sealed class StubLocalizer : ILocalizer
    {
        private static readonly Dictionary<string, string> Values = new(StringComparer.Ordinal)
        {
            ["runningGames.origin.current"] = "本次启动",
            ["runningGames.origin.recovered"] = "ECL 重启前已运行",
            ["runningGames.pid"] = "PID {0}",
        };

        public CultureInfo Culture => CultureInfo.InvariantCulture;

        public IReadOnlyList<CultureInfo> AvailableCultures => [CultureInfo.InvariantCulture];

        public string Get(string key) => Values.TryGetValue(key, out var value) ? value : key;

        public string Get(string key, params object?[] arguments) =>
            string.Format(Culture, Get(key), arguments);
    }
}
