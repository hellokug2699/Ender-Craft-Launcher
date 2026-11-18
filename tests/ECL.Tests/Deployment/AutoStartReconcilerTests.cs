// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Configuration.Settings;
using Ecl.Deployment.Startup;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ecl.Tests.Deployment;

/// <summary>
/// F-12 登录启动对齐：注册表跟随设置，但设置永远不被 reconcile 改写；
/// 修复失败必须可见（返回错误 + 日志），不能静默假装已应用。
/// </summary>
public sealed class AutoStartReconcilerTests
{
    private static AutoStartReconciler Create(
        FakeRunKeyStore store,
        DeploymentTestSettings settings,
        FakeExecutableLocator? locator = null) =>
        new(
            new AutoStartRegistration(store, locator ?? new FakeExecutableLocator(), NullLogger<AutoStartRegistration>.Instance),
            settings,
            NullLogger<AutoStartReconciler>.Instance);

    [Fact]
    public void EnabledSettingWritesTheStartupEntry()
    {
        var store = new FakeRunKeyStore();
        var settings = new DeploymentTestSettings();
        settings.Values[LauncherKeys.StartWithWindows] = "true";

        var result = Create(store, settings).Reconcile();

        Assert.True(result.IsSuccess);
        Assert.Equal("\"C:\\ECL\\ECL.Bootstrapper.exe\" --autostart", store.Values[RunKeyNames.ValueName]);
        Assert.Empty(settings.Writes);
    }

    [Fact]
    public void DisabledSettingRemovesAStaleStartupEntry()
    {
        var store = new FakeRunKeyStore();
        store.Values[RunKeyNames.ValueName] = "\"C:\\Old\\ECL.Bootstrapper.exe\" --autostart";
        var settings = new DeploymentTestSettings();

        var result = Create(store, settings).Reconcile();

        Assert.True(result.IsSuccess);
        Assert.False(store.Values.ContainsKey(RunKeyNames.ValueName));
        Assert.Empty(settings.Writes);
    }

    [Fact]
    public void MatchingStateIsNotRewritten()
    {
        var store = new FakeRunKeyStore();
        var settings = new DeploymentTestSettings();
        settings.Values[LauncherKeys.StartWithWindows] = "true";
        var reconciler = Create(store, settings);

        Assert.True(reconciler.Reconcile().IsSuccess);
        var writesAfterFirstRun = store.WriteCount;

        // 幂等：状态已经一致时不应重复写注册表。
        Assert.True(reconciler.Reconcile().IsSuccess);
        Assert.Equal(writesAfterFirstRun, store.WriteCount);
    }

    [Fact]
    public void StaleCommandIsRepaired()
    {
        var store = new FakeRunKeyStore();
        store.Values[RunKeyNames.ValueName] = "\"C:\\Moved\\ECL.Bootstrapper.exe\" --autostart";
        var settings = new DeploymentTestSettings();
        settings.Values[LauncherKeys.StartWithWindows] = "true";

        var result = Create(store, settings).Reconcile();

        Assert.True(result.IsSuccess);
        Assert.Equal("\"C:\\ECL\\ECL.Bootstrapper.exe\" --autostart", store.Values[RunKeyNames.ValueName]);
    }

    [Fact]
    public void RepairFailureIsVisibleAndDoesNotRewriteUserSetting()
    {
        var store = new FakeRunKeyStore { WriteFails = true };
        var settings = new DeploymentTestSettings();
        settings.Values[LauncherKeys.StartWithWindows] = "true";

        var result = Create(store, settings).Reconcile();

        Assert.False(result.IsSuccess);
        Assert.Equal("autostart.registry_failed", result.Error!.Code);
        // 关键边界：修复失败只影响注册表，用户“想开机启动”的意图必须保留。
        Assert.Equal("true", settings.Values[LauncherKeys.StartWithWindows]);
        Assert.Empty(settings.Writes);
    }

    [Fact]
    public void UnsupportedRegistrationReportsReasonWithoutTouchingSettings()
    {
        var store = new FakeRunKeyStore();
        var settings = new DeploymentTestSettings();
        settings.Values[LauncherKeys.StartWithWindows] = "true";

        var result = Create(store, settings, new FakeExecutableLocator { IsDotnetHosted = true }).Reconcile();

        Assert.False(result.IsSuccess);
        Assert.Equal("autostart.unsupported", result.Error!.Code);
        Assert.Empty(store.Values);
        Assert.Empty(settings.Writes);
    }

    [Fact]
    public void ReadFailureIsReportedInsteadOfSilentlyDisabling()
    {
        var store = new FakeRunKeyStore { ReadFails = true };
        var settings = new DeploymentTestSettings();

        var result = Create(store, settings).Reconcile();

        Assert.False(result.IsSuccess);
        Assert.Equal("autostart.read_failed", result.Error!.Code);
        Assert.Equal(0, store.WriteCount);
        Assert.Equal(0, store.DeleteCount);
    }
}
