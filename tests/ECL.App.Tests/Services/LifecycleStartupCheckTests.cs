// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.App.Dialogs;
using Ecl.App.Dialogs.ViewModels;
using Ecl.App.Services;
using Ecl.Core;
using Ecl.Core.Localization;
using Ecl.Minecraft.Engine.Lifecycle;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ecl.App.Tests.Services;

/// <summary>
/// F-03 §5.8 启动自检：只报告生命周期状态，不自动恢复；没有待处理状态时不打扰用户。
/// </summary>
public sealed class LifecycleStartupCheckTests
{
    private static readonly ILocalizer Localizer =
        new JsonLocalizer(System.Globalization.CultureInfo.GetCultureInfo("zh-CN"));

    [Fact]
    public async Task NoOutstandingLifecycleStateDoesNotPrompt()
    {
        var recovery = new FakeRecoveryService { Report = Report(entries: 0, orphans: 0) };
        var dialogs = new FakeDialogEngine();
        var check = Create(recovery, dialogs);

        var report = await check.RunAsync();

        Assert.NotNull(report);
        Assert.Empty(dialogs.Requests);
    }

    [Fact]
    public async Task NeedsRecoveryPromptsOnceWithActionableSummary()
    {
        var recovery = new FakeRecoveryService
        {
            Report = Report(entries: 0, orphans: 0, needsRecovery: 2, resumable: 1),
        };
        var dialogs = new FakeDialogEngine();
        var check = Create(recovery, dialogs);

        await check.RunAsync();

        var request = Assert.Single(dialogs.Requests);
        Assert.Equal(DialogSeverity.Warning, request.Severity);
        Assert.Equal(Localizer.Get("lifecycle.startup.title"), request.Title);
        var content = Assert.IsType<MessageDialogViewModel>(request.Content);
        Assert.Contains("需要恢复 2 项", content.Message);
        Assert.Contains("可继续 1 项", content.Message);
        // 提示里不得出现本地绝对路径。
        Assert.DoesNotContain(":\\", content.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OrphansAndCleanableSourcesAlsoPrompt()
    {
        var recovery = new FakeRecoveryService
        {
            Report = Report(entries: 0, orphans: 1, cleanableSource: 1),
        };
        var dialogs = new FakeDialogEngine();
        var check = Create(recovery, dialogs);

        await check.RunAsync();

        var request = Assert.Single(dialogs.Requests);
        var content = Assert.IsType<MessageDialogViewModel>(request.Content);
        Assert.Contains("可清理退休源 1 项", content.Message);
        Assert.Contains("残留 1 项", content.Message);
    }

    [Fact]
    public async Task InspectionFailureDoesNotBlockStartupAndShowsNothing()
    {
        var recovery = new FakeRecoveryService
        {
            Failure = new EclError("lifecycle.inspect_failed", "读取失败。"),
        };
        var dialogs = new FakeDialogEngine();
        var check = Create(recovery, dialogs);

        var report = await check.RunAsync();

        Assert.Null(report);
        Assert.Empty(dialogs.Requests);
    }

    [Fact]
    public async Task UnavailableDialogHostOnlyLogs()
    {
        var recovery = new FakeRecoveryService
        {
            Report = Report(entries: 0, orphans: 0, needsRecovery: 1),
        };
        var dialogs = new FakeDialogEngine { IsAvailable = false };
        var check = Create(recovery, dialogs);

        var report = await check.RunAsync();

        Assert.NotNull(report);
        Assert.Empty(dialogs.Requests);
    }

    private static LifecycleStartupCheck Create(FakeRecoveryService recovery, FakeDialogEngine dialogs) =>
        new(recovery, dialogs, Localizer, NullLogger<LifecycleStartupCheck>.Instance);

    private static InstanceLifecycleRecoveryReport Report(
        int entries,
        int orphans,
        int needsRecovery = 0,
        int resumable = 0,
        int cleanableSource = 0)
    {
        var now = DateTimeOffset.UtcNow;
        var records = new List<InstanceLifecycleRecoveryEntry>();
        AddRecords(records, now, entries, "Completed", "none");
        AddRecords(records, now, needsRecovery, "NeedsRecovery", "inspect");
        AddRecords(records, now, resumable, "Interrupted", "resume");
        AddRecords(records, now, cleanableSource, "Completed", "cleanup_source");

        var orphanList = new List<InstanceLifecycleOrphan>();
        for (var index = 0; index < orphans; index++)
        {
            orphanList.Add(new InstanceLifecycleOrphan(
                "quarantine",
                $"C:\\data\\.ecl-trash\\{index}",
                $"instance-{index}",
                "无 journal 对应的残留。"));
        }

        return new InstanceLifecycleRecoveryReport(now, records.ToArray(), orphanList.ToArray());
    }

    private static void AddRecords(
        List<InstanceLifecycleRecoveryEntry> records,
        DateTimeOffset now,
        int count,
        string state,
        string action)
    {
        for (var index = 0; index < count; index++)
        {
            var id = records.Count;
            records.Add(new InstanceLifecycleRecoveryEntry(
                new InstanceLifecycleRecord(
                    $"plan-{id}",
                    $"instance-{id}",
                    InstanceLifecycleKind.Move,
                    state,
                    now,
                    now,
                    $"C:\\data\\journal-{id}.json"),
                action,
                []));
        }
    }

    private sealed class FakeRecoveryService : IInstanceLifecycleRecoveryService
    {
        public InstanceLifecycleRecoveryReport? Report { get; set; }

        public EclError? Failure { get; set; }

        public Task<Result<InstanceLifecycleRecoveryReport>> InspectAsync(
            string? instanceId = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Failure is { } error
                ? Result<InstanceLifecycleRecoveryReport>.Fail(error)
                : Result<InstanceLifecycleRecoveryReport>.Ok(Report ?? new InstanceLifecycleRecoveryReport(
                    DateTimeOffset.UtcNow,
                    [],
                    [])));

        public Task<Result<InstanceLifecycleRecord>> ResumeAsync(
            string planId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<InstanceLifecycleRecord>> DiscardAsync(
            string planId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<InstanceLifecycleSourceCleanupResult>> CleanupSourceAsync(
            string planId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeDialogEngine : IDialogEngine
    {
        public bool IsAvailable { get; set; } = true;

        public List<DialogRequest> Requests { get; } = [];

        public DialogResult Show(DialogRequest request)
        {
            Requests.Add(request);
            return new DialogResult(DialogOutcome.Primary);
        }

        public Task<DialogResult> ShowAsync(
            DialogRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new DialogResult(DialogOutcome.Primary));
        }

        public bool CloseTop(DialogOutcome outcome) => false;
    }
}
