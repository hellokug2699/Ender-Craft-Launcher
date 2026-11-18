// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.App.Dialogs;
using Ecl.App.Services;
using Ecl.App.Services.Confirmation;
using Ecl.App.Tests.Support;
using Ecl.Configuration.Settings;
using Ecl.Core.Localization;
using Xunit;

namespace Ecl.App.Tests.Dialogs;

/// <summary>
/// F-12 结果弹窗自动关闭：
/// - 到点以 <see cref="DialogOutcome.Closed"/> 结束，绝不伪造主操作；
/// - 内容 Busy 时不关闭；
/// - 关键结果与管理开关都不会自动关闭；
/// - 用户先关闭时计时器取消，不会出现第二次关闭。
/// </summary>
public sealed class DialogAutoCloseTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly ILocalizer Localizer =
        new JsonLocalizer(System.Globalization.CultureInfo.GetCultureInfo("zh-CN"));

    [Fact]
    public async Task DialogClosesWithClosedOutcomeAfterTheConfiguredDelay()
    {
        using var facts = Create();
        var task = facts.Ui.Invoke(() => facts.Engine.ShowAsync(Request(TimeSpan.FromSeconds(5))));
        var entry = facts.Ui.Invoke(() => facts.Layer.Top);
        Assert.NotNull(entry);

        facts.Time.Advance(TimeSpan.FromSeconds(4));
        var early = await Task.WhenAny(task, Task.Delay(200));
        Assert.NotSame(task, early);

        facts.Time.Advance(TimeSpan.FromSeconds(1));
        var result = await task.WaitAsync(Timeout);

        // 自动关闭是“窗口关了”，不是“用户点了确定”。
        Assert.Equal(DialogOutcome.Closed, result.Outcome);
        Assert.Empty(facts.Ui.Invoke(() => facts.Layer.Entries.ToArray()));
    }

    [Fact]
    public async Task BusyDialogIsNotAutoClosed()
    {
        using var facts = Create();
        var task = facts.Ui.Invoke(() => facts.Engine.ShowAsync(Request(TimeSpan.FromSeconds(5))));
        var entry = facts.Ui.Invoke(() => facts.Layer.Top)!;
        facts.Ui.Invoke(() => entry.SetBusy(true));

        facts.Time.Advance(TimeSpan.FromSeconds(5));
        var early = await Task.WhenAny(task, Task.Delay(300));

        Assert.NotSame(task, early);
        Assert.True(facts.Ui.Invoke(() => entry.IsOpen));

        // 计时器已经触发过一次，解除 Busy 后不会补一次关闭。
        facts.Ui.Invoke(() => entry.SetBusy(false));
        var stillOpen = await Task.WhenAny(task, Task.Delay(200));
        Assert.NotSame(task, stillOpen);
        Assert.True(facts.Ui.Invoke(() => entry.IsOpen));

        facts.Ui.Invoke(() => entry.TryClose(DialogOutcome.Primary));
        Assert.Equal(DialogOutcome.Primary, (await task.WaitAsync(Timeout)).Outcome);
    }

    [Fact]
    public async Task UserCloseCancelsTheAutoCloseTimer()
    {
        using var facts = Create();
        var task = facts.Ui.Invoke(() => facts.Engine.ShowAsync(Request(TimeSpan.FromSeconds(5))));
        var entry = facts.Ui.Invoke(() => facts.Layer.Top)!;

        facts.Ui.Invoke(() => entry.TryClose(DialogOutcome.Primary));
        var result = await task.WaitAsync(Timeout);

        facts.Time.Advance(TimeSpan.FromSeconds(30));
        await Task.Delay(100);

        Assert.Equal(DialogOutcome.Primary, result.Outcome);
        Assert.Empty(facts.Ui.Invoke(() => facts.Layer.Entries.ToArray()));
    }

    [Fact]
    public async Task RequestWithoutDelayNeverAutoCloses()
    {
        using var facts = Create();
        var task = facts.Ui.Invoke(() => facts.Engine.ShowAsync(Request(delay: null)));

        facts.Time.Advance(TimeSpan.FromHours(1));
        var early = await Task.WhenAny(task, Task.Delay(200));

        Assert.NotSame(task, early);
        facts.Ui.Invoke(() => facts.Layer.Top!.TryClose(DialogOutcome.Cancel));
        Assert.Equal(DialogOutcome.Cancel, (await task.WaitAsync(Timeout)).Outcome);
    }

    [Theory]
    [InlineData(true, false, 8)]
    [InlineData(false, false, 0)]
    [InlineData(true, true, 0)]
    public async Task ResultDialogAutoClosesOnlyForNonCriticalSuccess(
        bool autoCloseEnabled,
        bool isCritical,
        int expectedSeconds)
    {
        var settings = new FakeSettingsService();
        settings.Values[LauncherKeys.AutoCloseSuccessDialogs] = autoCloseEnabled ? "true" : "false";
        var engine = new CapturingDialogEngine();
        var service = new ResultDialogService(settings, engine, Localizer);

        await service.ShowResultAsync(new ResultDialogRequest("完成", "实例已创建。", isCritical));

        var request = Assert.Single(engine.Requests);
        if (expectedSeconds == 0)
        {
            Assert.Null(request.AutoCloseAfter);
        }
        else
        {
            Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), request.AutoCloseAfter);
        }
    }

    [Fact]
    public async Task ResultDialogFallsBackWhenTheHostIsUnavailableAndNeverFakesConfirmation()
    {
        var settings = new FakeSettingsService();
        var engine = new CapturingDialogEngine { IsAvailable = false };
        var fallback = new RecordingDialogService();
        var service = new ResultDialogService(settings, engine, Localizer, fallback);

        var result = await service.ShowResultAsync(new ResultDialogRequest("失败", "无法删除实例。", IsCritical: true));

        Assert.Equal(DialogOutcome.Closed, result.Outcome);
        Assert.False(result.IsPrimary);
        Assert.Empty(engine.Requests);
        Assert.Equal("失败", fallback.LastTitle);
    }

    private static DialogRequest Request(TimeSpan? delay) => new()
    {
        Title = "测试弹窗",
        Content = new object(),
        AutoCloseAfter = delay,
    };

    private static Facts Create()
    {
        var ui = new UiThread();
        var time = new FakeTimeProvider(Start);
        var layer = ui.Invoke(() => new DialogLayerViewModel(ui.Dispatcher));
        ui.Invoke(layer.AttachHost);
        var engine = ui.Invoke(() => new DialogEngine(layer, time));
        return new Facts(ui, layer, engine, time);
    }

    private sealed class Facts(UiThread ui, DialogLayerViewModel layer, DialogEngine engine, FakeTimeProvider time)
        : IDisposable
    {
        public UiThread Ui { get; } = ui;

        public DialogLayerViewModel Layer { get; } = layer;

        public DialogEngine Engine { get; } = engine;

        public FakeTimeProvider Time { get; } = time;

        public void Dispose()
        {
            Engine.Dispose();
            Ui.Dispose();
        }
    }

    private sealed class CapturingDialogEngine : IDialogEngine
    {
        public bool IsAvailable { get; set; } = true;

        public List<DialogRequest> Requests { get; } = [];

        public DialogResult Show(DialogRequest request)
        {
            Requests.Add(request);
            return new DialogResult(DialogOutcome.Primary);
        }

        public Task<DialogResult> ShowAsync(DialogRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new DialogResult(DialogOutcome.Primary));
        }

        public bool CloseTop(DialogOutcome outcome) => false;
    }

    private sealed class RecordingDialogService : IDialogService
    {
        public string? LastTitle { get; private set; }

        public bool Confirm(
            string title,
            string message,
            string? acknowledgementToken = null,
            ConfirmationKind kind = ConfirmationKind.Mandatory) => true;

        public InstanceDeletionChoice ChooseInstanceDeletion(
            string title,
            string message,
            bool canDeleteManagedData) => InstanceDeletionChoice.Cancel;

        public void ShowInfo(string title, string message) => LastTitle = title;

        public void ShowError(string title, string message) => LastTitle = title;

        public string? PickOpenFile(string title, string filter) => null;

        public IReadOnlyList<string> PickOpenFiles(string title, string filter) => [];

        public string? PickSaveFile(string title, string filter, string? defaultFileName = null) => null;

        public string? PickFolder(string title) => null;

        public bool OpenUrl(string url) => false;
    }
}
