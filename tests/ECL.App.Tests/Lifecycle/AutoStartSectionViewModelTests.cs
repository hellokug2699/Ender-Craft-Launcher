// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.IO;
using Ecl.App.Tests.Support;
using Ecl.App.ViewModels.Settings;
using Ecl.Configuration;
using Ecl.Configuration.Settings;
using Ecl.Core;
using Ecl.Core.Localization;
using Ecl.Deployment.Startup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ecl.App.Tests.Lifecycle;

/// <summary>
/// F-12 设置 → 常规的登录启动分区：真实注册表状态展示、写入失败回滚、重新应用，
/// 以及“重置设置不等于删除注册表项”。注册表本身由手写替身表达，不触碰真实 HKCU。
/// </summary>
public sealed class AutoStartSectionViewModelTests
{
    [Fact]
    public void EnablingWritesRegistryAndKeepsUserIntent()
    {
        using var host = new Host();
        host.Registration.State = new AutoStartState(false, null, true, null, null);

        host.Section.Option.BoolValue = true;

        Assert.Equal([true], host.Registration.SetCalls);
        Assert.Equal("true", host.Settings.GetRawString(LauncherKeys.StartWithWindows));
        Assert.False(host.Section.HasError);
        Assert.True(host.Section.IsRegistered);
        Assert.Contains("已", host.Section.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void RegistryWriteFailureRollsBackTheSettingAndShowsTheReason()
    {
        using var host = new Host();
        host.Registration.FailWrites = true;

        host.Section.Option.BoolValue = true;

        Assert.Equal([true], host.Registration.SetCalls);
        // 设置必须回到上一次真实生效值，界面不能显示“已开启”而注册表没写进去。
        Assert.Equal("false", host.Settings.GetRawString(LauncherKeys.StartWithWindows));
        Assert.False(host.Section.Option.BoolValue);
        Assert.True(host.Section.HasError);
        Assert.Contains("回滚", host.Section.ErrorText, StringComparison.Ordinal);
    }

    [Fact]
    public void ReapplyAlignsRegistryWithoutRewritingSetting()
    {
        using var host = new Host();
        host.Settings.TrySetRaw(LauncherKeys.StartWithWindows, "true", null, out _);
        host.Registration.State = new AutoStartState(false, null, true, null, null);
        var section = host.RecreateSection();

        section.ReapplyCommand.Execute(null);

        Assert.Equal([true], host.Registration.SetCalls);
        Assert.Equal("true", host.Settings.GetRawString(LauncherKeys.StartWithWindows));
        Assert.True(section.IsRegistered);
        Assert.False(section.HasError);
    }

    [Fact]
    public void UnsupportedRunModeKeepsIntentButExplainsItCannotApply()
    {
        using var host = new Host();
        host.Registration.State = new AutoStartState(
            false,
            null,
            IsSupported: false,
            "autostart.unsupported",
            "dotnet 宿主不支持登录启动注册。");
        host.RecreateSection();

        host.Section.Option.BoolValue = true;

        // 不支持不是写入失败：保留用户意图，但必须显示准确原因。
        Assert.Empty(host.Registration.SetCalls);
        Assert.Equal("true", host.Settings.GetRawString(LauncherKeys.StartWithWindows));
        Assert.True(host.Section.HasError);
        Assert.Contains("不支持", host.Section.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void ResettingSettingsDoesNotTouchTheRegistry()
    {
        using var host = new Host();
        host.Registration.State = new AutoStartState(
            true,
            "\"C:\\ECL\\ECL.exe\" --autostart",
            true,
            null,
            null);
        var section = host.RecreateSection();
        Assert.True(section.IsRegistered);

        section.OnSettingsReset();

        Assert.Empty(host.Registration.SetCalls);
    }

    private sealed class Host : IDisposable
    {
        private readonly string _root;
        private readonly ServiceProvider _provider;

        public Host()
        {
            _root = Path.Combine(Path.GetTempPath(), "ecl-autostart-tests", Guid.NewGuid().ToString("N"));
            var paths = new AppPaths(_root);
            paths.EnsureCreated();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddEclCore(paths, CultureInfo.GetCultureInfo("zh-CN"));
            services.AddEclConfiguration();
            _provider = services.BuildServiceProvider();

            Settings = _provider.GetRequiredService<ISettingsService>();
            Localizer = _provider.GetRequiredService<ILocalizer>();
            Section = RecreateSection();
        }

        public ISettingsService Settings { get; }

        public ILocalizer Localizer { get; }

        public FakeAutoStartRegistration Registration { get; } = new();

        public AutoStartSectionViewModel Section { get; private set; } = null!;

        /// <summary>用当前设置/注册表替身重建分区，覆盖“页面重新进入时读取真实状态”。</summary>
        public AutoStartSectionViewModel RecreateSection()
        {
            var reconciler = new AutoStartReconciler(
                Registration,
                Settings,
                NullLogger<AutoStartReconciler>.Instance);
            Section = new AutoStartSectionViewModel(
                Registration,
                reconciler,
                Settings,
                Localizer,
                NullLogger<AutoStartSectionViewModel>.Instance);
            return Section;
        }

        public void Dispose()
        {
            _provider.Dispose();
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch (IOException)
            {
                // 清理失败不影响断言。
            }
            catch (UnauthorizedAccessException)
            {
                // 同上。
            }
        }
    }
}
