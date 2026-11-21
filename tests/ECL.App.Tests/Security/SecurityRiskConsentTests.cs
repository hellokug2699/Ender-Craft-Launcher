// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using Ecl.App.Dialogs;
using Ecl.App.Dialogs.ViewModels;
using Ecl.App.Services;
using Ecl.App.Tests.Support;
using Ecl.Core.Localization;
using Ecl.Minecraft.Launch;
using Ecl.Minecraft.Security;
using Xunit;

namespace Ecl.App.Tests.Security;

/// <summary>
/// ADR 0043 §6 的界面侧：启动前风险披露必须明确列出风险、给出「继续/自行处理/取消」三种选择，
/// 默认焦点在取消，且只显示实例内相对路径。取消、关闭或宿主不可用都不得被当成接受。
/// </summary>
public sealed class SecurityRiskConsentTests
{
    private static readonly ILocalizer Localizer = new JsonLocalizer(CultureInfo.GetCultureInfo("zh-CN"));

    [Fact]
    public async Task PrimaryChoiceAcceptsAndDisclosesFindingsWithoutFullPaths()
    {
        var engine = new FakeDialogEngine();
        engine.Results.Enqueue(new DialogResult(DialogOutcome.Primary));
        var consent = Create(engine, out _);

        var accepted = await consent.ConfirmAsync(Request(Finding("mods/evil.jar", ThreatLevel.Suspicious)));

        Assert.True(accepted);
        var request = Assert.Single(engine.Requests);
        Assert.Equal("启动前需要确认安全风险", request.Title);
        Assert.Equal(DialogInitialFocus.Cancel, request.InitialFocus);
        Assert.Equal(DialogSeverity.Error, request.Severity);

        var content = Assert.IsType<MessageDialogViewModel>(request.Content);
        Assert.Equal("无视风险，继续启动", content.PrimaryText);
        Assert.Equal("先自行处理", content.SecondaryText);
        Assert.Equal("取消", content.CancelText);
        Assert.Contains(@"mods\evil.jar", content.Message);
        Assert.Contains("可疑", content.Message);
        Assert.Contains("不会把风险等级改为 Clean", content.Message);
        Assert.Contains("安全中心", content.Message);
    }

    [Fact]
    public async Task AbsolutePathsAreReducedToTheFileName()
    {
        var engine = new FakeDialogEngine();
        engine.Results.Enqueue(new DialogResult(DialogOutcome.Primary));
        var consent = Create(engine, out _);

        await consent.ConfirmAsync(Request(Finding(@"C:\evil\1.ps1", ThreatLevel.HighRisk)));

        var content = Assert.IsType<MessageDialogViewModel>(Assert.Single(engine.Requests).Content);
        Assert.Contains("1.ps1", content.Message);
        Assert.DoesNotContain(@"C:\", content.Message);
        Assert.Contains("高风险", content.Message);
    }

    [Fact]
    public async Task SecondaryAndClosedOutcomesAreNotAccepted()
    {
        var engine = new FakeDialogEngine();
        engine.Results.Enqueue(new DialogResult(DialogOutcome.Secondary));
        engine.Results.Enqueue(DialogResult.Closed);
        engine.Results.Enqueue(DialogResult.Cancel);
        var consent = Create(engine, out _);

        Assert.False(await consent.ConfirmAsync(Request(Finding("mods/a.jar", ThreatLevel.Suspicious))));
        Assert.False(await consent.ConfirmAsync(Request(Finding("mods/a.jar", ThreatLevel.Suspicious))));
        Assert.False(await consent.ConfirmAsync(Request(Finding("mods/a.jar", ThreatLevel.Suspicious))));
    }

    [Fact]
    public async Task UnavailableHostFallsBackToTheMandatoryConfirmation()
    {
        var engine = new FakeDialogEngine { IsAvailable = false };
        var consent = Create(engine, out var dialogs);

        dialogs.ConfirmResult = true;
        Assert.True(await consent.ConfirmAsync(Request(Finding("mods/a.jar", ThreatLevel.Suspicious))));
        Assert.Contains("无视风险", dialogs.LastMessage ?? string.Empty);

        dialogs.ConfirmResult = false;
        Assert.False(await consent.ConfirmAsync(Request(Finding("mods/a.jar", ThreatLevel.Suspicious))));
    }

    [Fact]
    public async Task FindingsBeyondTheLimitAreOnlyCounted()
    {
        var engine = new FakeDialogEngine();
        engine.Results.Enqueue(new DialogResult(DialogOutcome.Primary));
        var consent = Create(engine, out _);

        var findings = Enumerable.Range(0, 9)
            .Select(index => Finding($"mods/mod-{index}.jar", ThreatLevel.Suspicious))
            .ToArray();
        await consent.ConfirmAsync(new SecurityRiskConsentRequest(
            "instance-1",
            "Demo",
            findings,
            "检测到 9 项需要确认的风险；本次启动已暂停。"));

        var content = Assert.IsType<MessageDialogViewModel>(Assert.Single(engine.Requests).Content);
        Assert.Contains(@"mods\mod-0.jar", content.Message);
        Assert.DoesNotContain(@"mods\mod-6.jar", content.Message);
        Assert.Contains("另有 3 项未列出", content.Message);
    }

    private static SecurityRiskConsent Create(FakeDialogEngine engine, out FakeConsentDialogService dialogs)
    {
        dialogs = new FakeConsentDialogService();
        return new SecurityRiskConsent(engine, dialogs, Localizer);
    }

    private static SecurityRiskConsentRequest Request(params ThreatFinding[] findings) =>
        new("instance-1", "Demo", findings, "检测到需要确认的风险；本次启动已暂停。");

    private static ThreatFinding Finding(string relativePath, ThreatLevel level) => new(
        $"{relativePath}-1",
        relativePath,
        "hash",
        level,
        [new ThreatEvidence("file.executable", "2026-11-03", level, "evidence")],
        "recommendation");

    private sealed class FakeConsentDialogService : IDialogService
    {
        public bool ConfirmResult { get; set; }

        public string? LastMessage { get; private set; }

        public bool Confirm(
            string title,
            string message,
            string? acknowledgementToken = null,
            ConfirmationKind kind = ConfirmationKind.Mandatory)
        {
            LastMessage = message;
            return ConfirmResult;
        }

        public InstanceDeletionChoice ChooseInstanceDeletion(
            string title,
            string message,
            bool canDeleteManagedData) => InstanceDeletionChoice.Cancel;

        public void ShowInfo(string title, string message)
        {
        }

        public void ShowError(string title, string message)
        {
        }

        public string? PickOpenFile(string title, string filter) => null;

        public IReadOnlyList<string> PickOpenFiles(string title, string filter) => [];

        public string? PickSaveFile(string title, string filter, string? defaultFileName = null) => null;

        public string? PickFolder(string title) => null;

        public bool OpenUrl(string url) => false;
    }
}
