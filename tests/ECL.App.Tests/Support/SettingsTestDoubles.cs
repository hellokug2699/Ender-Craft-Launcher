// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO;
using Ecl.App.Services;
using Ecl.App.Services.Lifecycle;
using Ecl.App.Services.Updates;
using Ecl.App.Shell.Lifecycle;
using Ecl.App.ViewModels.Settings;
using Ecl.Configuration.Settings;
using Ecl.Core;
using Ecl.Core.Localization;
using Ecl.Deployment;
using Ecl.Deployment.Startup;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ecl.App.Tests.Support;

/// <summary>
/// 登录启动注册替身：真实状态由 <see cref="State"/> 表达，写入可被注入失败，
/// 因此“注册表失败 → 回滚设置”与“重新应用”都能确定性地验证，而不触碰真实 HKCU。
/// </summary>
internal sealed class FakeAutoStartRegistration : IAutoStartRegistration
{
    public AutoStartState State { get; set; } = new(false, null, true, null, null);

    public string? CommandToWrite { get; set; } = "\"C:\\ECL\\ECL.exe\" --autostart";

    public bool FailWrites { get; set; }

    public bool FailReads { get; set; }

    public List<bool> SetCalls { get; } = [];

    public string? ExpectedCommand => State.IsSupported ? CommandToWrite : null;

    public AutoStartState Read()
    {
        if (FailReads)
        {
            throw new IOException("registry read failed");
        }

        return State;
    }

    public Result SetEnabled(bool enabled)
    {
        SetCalls.Add(enabled);
        if (FailWrites)
        {
            return Result.Fail("autostart.registry_failed", "无法写入 Windows 登录启动项。");
        }

        State = enabled
            ? new AutoStartState(true, CommandToWrite, true, null, null)
            : new AutoStartState(false, null, true, null, null);
        return Result.Ok();
    }
}

/// <summary>可编程的更新调度器：不触网，只返回队列中的结论并记录调用次数。</summary>
internal sealed class FakeUpdateCheckScheduler : IUpdateCheckScheduler
{
    public Queue<UpdateCheckReport> Reports { get; } = new();

    public int Calls { get; private set; }

    public int StartCalls { get; private set; }

    public UpdateCheckReport? LastReport { get; private set; }

    public event EventHandler<UpdateCheckReport>? ReportAvailable;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        StartCalls++;
        return Task.CompletedTask;
    }

    public Task<UpdateCheckReport?> CheckNowAsync(bool userInitiated, CancellationToken cancellationToken = default)
    {
        Calls++;
        if (Reports.Count == 0)
        {
            return Task.FromResult(LastReport);
        }

        var report = Reports.Dequeue();
        LastReport = report;
        ReportAvailable?.Invoke(this, report);
        return Task.FromResult<UpdateCheckReport?>(report);
    }
}

/// <summary>构造常规设置页的测试工厂：页面只在生产与测试两处装配，依赖全部显式传入。</summary>
internal static class SettingsGeneralPageFactory
{
    public static SettingsGeneralPageViewModel Create(
        ISettingsService settings,
        ILocalizer localizer,
        IDialogService dialogs,
        CodeSigningTrustService codeSigning,
        ITrayHost? tray = null,
        IAutoStartRegistration? registration = null,
        IUpdateCheckScheduler? scheduler = null)
    {
        var autoStartRegistration = registration ?? new FakeAutoStartRegistration();
        var reconciler = new AutoStartReconciler(
            autoStartRegistration,
            settings,
            NullLogger<AutoStartReconciler>.Instance);
        var section = new AutoStartSectionViewModel(
            autoStartRegistration,
            reconciler,
            settings,
            localizer,
            NullLogger<AutoStartSectionViewModel>.Instance);

        return new SettingsGeneralPageViewModel(
            settings,
            localizer,
            dialogs,
            codeSigning,
            section,
            scheduler ?? new FakeUpdateCheckScheduler(),
            tray ?? new FakeTrayHost(),
            NullLogger<SettingsGeneralPageViewModel>.Instance);
    }
}
