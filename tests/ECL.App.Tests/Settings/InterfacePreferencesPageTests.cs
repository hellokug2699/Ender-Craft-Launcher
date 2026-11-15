// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.IO;
using Ecl.App.Services;
using Ecl.App.Tests.Support;
using Ecl.App.ViewModels;
using Ecl.App.ViewModels.Settings;
using Ecl.Configuration;
using Ecl.Configuration.Settings;
using Ecl.Core;
using Ecl.Deployment;
using Ecl.Java;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ecl.App.Tests.Settings;

/// <summary>
/// 设置中心「界面偏好」页（settings-interface-preferences.md）：
/// 四组偏好（字体与文字 / 界面缩放 / 语言 / 动效）、只列随包已验证字体、
/// 缩放即时保存与恢复默认、语言“跟随系统 + 需重启”、动效开关，
/// 以及规格明确不提供的主题色/逐控件字号/字体导入等固定边界。
/// </summary>
public sealed class InterfacePreferencesPageTests
{
    [Fact]
    public void PageRendersTheFourGroupsAndOnlyBundledVerifiedFonts()
    {
        using var host = new PreferencesHost();
        var page = host.Page;

        Assert.Equal(
            ["launcher.interfaceFont", "launcher.fontSize", "launcher.uiScale", "launcher.language", "launcher.animationsEnabled"],
            page.Options.Select(option => option.Key));
        Assert.Equal(
            ["launcher.interfaceFont", "launcher.fontSize"],
            page.FontGroup.Options.Select(option => option.Key));
        Assert.Equal(["launcher.language"], page.LanguageGroup.Options.Select(option => option.Key));
        Assert.Equal(["launcher.animationsEnabled"], page.MotionGroup.Options.Select(option => option.Key));
        Assert.False(string.IsNullOrWhiteSpace(page.FontGroup.Note));
        Assert.False(string.IsNullOrWhiteSpace(page.LanguageGroup.Note));
        Assert.False(string.IsNullOrWhiteSpace(page.MotionGroup.Note));

        // 字体列表只包含随包提供、家族名与字面已验证的字体族。
        var font = page.FontGroup.Options.Single(option => option.Key == "launcher.interfaceFont");
        var choice = Assert.Single(font.Choices);
        Assert.Equal("harmonyos-sans-sc", choice.Value);
        Assert.Contains("HarmonyOS Sans SC", choice.DisplayName);
        Assert.Equal("harmonyos-sans-sc", font.TextValue);
        Assert.Same(choice, font.SelectedChoice);

        // 字号只有三档整体尺寸；低于标准的档位会跌破 ADR 0050 §5 的可读性底线，因此不提供。
        var fontSize = page.FontGroup.Options.Single(option => option.Key == "launcher.fontSize");
        Assert.Equal(["standard", "large", "xlarge"], fontSize.Choices.Select(item => item.Value));
        Assert.Equal(["标准", "较大", "最大"], fontSize.Choices.Select(item => item.DisplayName));
        Assert.Equal("standard", fontSize.TextValue);

        // 固定边界以文字说明呈现，不放置不生效的控件。
        Assert.NotEmpty(page.Boundaries.Items);
    }

    [Fact]
    public void PageHasNoThemeCustomizationOrPerControlFontSizeSettings()
    {
        using var host = new PreferencesHost();
        var page = host.Page;

        // 规格 §2/§8：不存在主题色、浅色模式或逐控件字号设置项；
        // 它们只作为“固定边界”的说明出现，搜索也找不到可编辑项。
        Assert.DoesNotContain(page.Options, option =>
            option.Key.Contains("theme", StringComparison.OrdinalIgnoreCase) ||
            option.Key.Contains("color", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(page.Boundaries.Items, item => item.Title.Contains("主题"));
        Assert.Contains(page.Boundaries.Items, item => item.Title.Contains("逐控件字号"));

        host.ViewModel.SearchText = "主题";
        Assert.DoesNotContain(
            host.ViewModel.SearchResults,
            result => result.OptionKey.Contains("theme", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FontFamilyAndTextSizeTierPersistImmediately()
    {
        using var host = new PreferencesHost();
        var page = host.Page;
        var fontSize = page.FontGroup.Options.Single(option => option.Key == "launcher.fontSize");

        fontSize.SelectedChoice = fontSize.Choices.Single(choice => choice.Value == "xlarge");

        Assert.Equal("xlarge", host.Settings.GetRawString("launcher.fontSize"));
        Assert.Equal(SettingsValueSource.Global, host.Settings.GetSource("launcher.fontSize"));
        Assert.Equal(1.25, UiTypography.GetFontSizeFactor(host.Settings.Get<string>("launcher.fontSize")));

        fontSize.SelectedChoice = fontSize.Choices.Single(choice => choice.Value == "large");
        Assert.Equal("large", host.Settings.GetRawString("launcher.fontSize"));
        Assert.Equal(1.1, UiTypography.GetFontSizeFactor("large"));
    }

    [Fact]
    public void UiScaleSnapsToStepsSavesImmediatelyAndResetsToTheDefault()
    {
        using var host = new PreferencesHost();
        var page = host.Page;

        Assert.Equal(100, page.UiScalePercent);
        Assert.True(page.IsUiScaleDefault);
        Assert.False(page.CanResetUiScale);

        page.UiScalePercent = 125;

        Assert.Equal(125, host.Settings.Get<int>("launcher.uiScale"));
        Assert.Equal(SettingsValueSource.Global, host.Settings.GetSource("launcher.uiScale"));
        Assert.Equal("125%", page.UiScaleValueText);
        Assert.False(page.IsUiScaleDefault);
        Assert.True(page.CanResetUiScale);

        // 步进 5%：非整档输入保存前先折算，配置里不会出现 83% 这种值。
        page.UiScalePercent = 83;
        Assert.Equal(85, host.Settings.Get<int>("launcher.uiScale"));
        Assert.Equal(85, page.UiScalePercent);

        // “恢复默认”清除键而不是写入 100，默认值以后调整时仍然生效。
        page.ResetUiScaleCommand.Execute(null);
        Assert.Equal(SettingsValueSource.Default, host.Settings.GetSource("launcher.uiScale"));
        Assert.Equal(100, page.UiScalePercent);
        Assert.Equal(100, UiTypography.ClampUiScale(host.Settings.Get<int>("launcher.uiScale")));
    }

    [Fact]
    public void UiScaleSaveFailureRestoresTheLastValidValueWithTheRealReason()
    {
        using var host = new PreferencesHost(settings => new FailingScaleSettings(settings));
        var page = host.Page;

        page.UiScalePercent = 130;

        // 保存失败：界面回到持久化值（默认 100），并显示真实原因。
        Assert.Equal(100, page.UiScalePercent);
        Assert.Contains("无法保存界面缩放", page.StatusMessage);
        Assert.Contains("磁盘只读", page.StatusMessage);
        Assert.Equal(SettingsValueSource.Default, host.Settings.GetSource("launcher.uiScale"));
    }

    [Fact]
    public void LanguageOffersFollowSystemAndStillRequiresARestart()
    {
        using var host = new PreferencesHost();
        var language = host.Page.LanguageGroup.Options.Single(option => option.Key == "launcher.language");

        Assert.Equal(["system", "zh-CN", "en-US"], language.Choices.Select(choice => choice.Value));
        Assert.Equal("跟随系统", language.Choices[0].DisplayName);
        Assert.Equal("system", language.TextValue);
        Assert.True(language.RequiresRestart);

        // 页面如实显示当前实际使用的语言（设置只是“跟随系统”）。
        Assert.False(string.IsNullOrWhiteSpace(host.Page.EffectiveLanguageText));

        language.SelectedChoice = language.Choices.Single(choice => choice.Value == "en-US");

        Assert.Equal("en-US", host.Settings.GetRawString("launcher.language"));
        Assert.Equal("en-US", SettingsBootstrap.ResolveLanguage(
            host.Settings.GetRawString("launcher.language"),
            CultureInfo.GetCultureInfo("zh-CN")));
    }

    [Fact]
    public void MotionTogglePersistsAndDrivesTheMotionPolicy()
    {
        using var host = new PreferencesHost();
        using var preferences = new UserInterfacePreferences(host.Settings);
        var motion = host.Page.MotionGroup.Options.Single(option => option.Key == "launcher.animationsEnabled");

        try
        {
            Assert.True(preferences.MotionEnabled);
            Assert.True(MotionSettings.UserEnabled);

            motion.BoolValue = false;

            Assert.Equal("false", host.Settings.GetRawString("launcher.animationsEnabled"));
            Assert.False(preferences.MotionEnabled);
            Assert.False(MotionSettings.UserEnabled);
            Assert.Equal(TimeSpan.Zero, MotionSettings.Normal);
        }
        finally
        {
            // 静态策略是进程级的：测试结束必须恢复，避免影响其它用例。
            MotionSettings.UserEnabled = true;
        }
    }

    [Fact]
    public void ResettingTheCategoryOnlyClearsInterfacePreferencesAfterConfirmation()
    {
        using var host = new PreferencesHost();
        host.Settings.TrySetRaw("launcher.checkUpdates", "false", null, out _);
        host.Settings.TrySetRaw("java.defaultMemoryMb", "8192", null, out _);
        host.Settings.TrySetRaw("launcher.uiScale", "125", null, out _);
        host.Settings.TrySetRaw("launcher.fontSize", "large", null, out _);
        host.Settings.TrySetRaw("launcher.animationsEnabled", "false", null, out _);
        var page = host.Page;

        host.Dialogs.ConfirmResult = false;
        page.ResetToDefaults();

        Assert.Equal("125", host.Settings.GetRawString("launcher.uiScale"));
        Assert.Equal("large", host.Settings.GetRawString("launcher.fontSize"));
        Assert.Equal("false", host.Settings.GetRawString("launcher.animationsEnabled"));

        host.Dialogs.ConfirmResult = true;
        page.ResetToDefaults();

        Assert.Equal(SettingsValueSource.Default, host.Settings.GetSource("launcher.uiScale"));
        Assert.Equal(SettingsValueSource.Default, host.Settings.GetSource("launcher.fontSize"));
        Assert.Equal(SettingsValueSource.Default, host.Settings.GetSource("launcher.animationsEnabled"));
        Assert.Equal(100, page.UiScalePercent);

        // 其它分类不受影响。
        Assert.Equal("false", host.Settings.GetRawString("launcher.checkUpdates"));
        Assert.Equal("8192", host.Settings.GetRawString("java.defaultMemoryMb"));
    }

    [Fact]
    public void SettingsSearchFindsTheScalePreferenceAndHighlightsIt()
    {
        using var host = new PreferencesHost();

        host.ViewModel.SearchText = "界面缩放";
        var result = Assert.Single(
            host.ViewModel.SearchResults,
            item => item.OptionKey == "launcher.uiScale");
        Assert.Equal(SettingsPageKeys.Interface, result.PageKey);

        host.ViewModel.OpenSearchResultCommand.Execute(result);

        Assert.Equal(SettingsPageKeys.Interface, host.ViewModel.Navigation.SelectedItem!.Key);
        Assert.True(host.Page.IsUiScaleHighlighted);
        Assert.Contains("已定位到", host.Page.StatusMessage);

        // 字体项同样可定位；定位另一项时缩放块不再高亮。
        host.ViewModel.SearchText = "界面字体";
        host.ViewModel.OpenSearchResultCommand.Execute(
            Assert.Single(host.ViewModel.SearchResults, item => item.OptionKey == "launcher.interfaceFont"));
        Assert.False(host.Page.IsUiScaleHighlighted);
        Assert.True(host.Page.FontGroup.Options[0].IsHighlighted);
    }

    private sealed class PreferencesHost : IDisposable
    {
        private readonly string _root;
        private readonly ServiceProvider _provider;

        public PreferencesHost(Func<ISettingsService, ISettingsService>? wrapSettings = null)
        {
            _root = Path.Combine(Path.GetTempPath(), "ecl-interface-tests", Guid.NewGuid().ToString("N"));
            var paths = new AppPaths(_root);
            paths.EnsureCreated();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddEclCore(paths, CultureInfo.GetCultureInfo("zh-CN"));
            services.AddEclConfiguration();
            _provider = services.BuildServiceProvider();

            var configured = _provider.GetRequiredService<ISettingsService>();
            Settings = wrapSettings?.Invoke(configured) ?? configured;
            Localizer = _provider.GetRequiredService<Ecl.Core.Localization.ILocalizer>();
            Dialogs = new FakeDialogService();

            var javaPage = new SettingsJavaPageViewModel(
                new FakeJavaLocator(),
                new JavaRuntimeRegistry(paths, NullLogger<JavaRuntimeRegistry>.Instance),
                new FakeJavaRuntimeValidator(),
                new FakeJavaRuntimeUninstallService(),
                Settings,
                Dialogs,
                paths,
                Localizer,
                NullLogger<SettingsJavaPageViewModel>.Instance);
            ViewModel = new SettingsViewModel(
                Settings,
                Localizer,
                new CodeSigningTrustService(Path.Combine(_root, "signing")),
                Dialogs,
                javaPage);

            // 生产环境里设置中心只有一个界面偏好页对象；测试必须用同一个实例，
            // 否则定位/高亮断言会指向另一个副本。
            Page = Assert.IsType<SettingsInterfacePageViewModel>(
                ViewModel.Navigation.Find(SettingsPageKeys.Interface)!.Page);
        }

        public ISettingsService Settings { get; }

        public Ecl.Core.Localization.ILocalizer Localizer { get; }

        public FakeDialogService Dialogs { get; }

        public SettingsInterfacePageViewModel Page { get; }

        public SettingsViewModel ViewModel { get; }

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
                // 测试清理失败不应影响测试结果。
            }
            catch (UnauthorizedAccessException)
            {
                // 同上。
            }
        }
    }

    /// <summary>只让 <c>launcher.uiScale</c> 写入失败的配置服务，用于验证“保存失败回到有效值”。</summary>
    private sealed class FailingScaleSettings(ISettingsService inner) : ISettingsService
    {
        public IReadOnlyList<OptionDefinition> GetDefinitions(OptionScope? scope = null) =>
            inner.GetDefinitions(scope);

        public OptionDefinition GetDefinition(string key) => inner.GetDefinition(key);

        public T? Get<T>(string key, string? instanceId = null) => inner.Get<T>(key, instanceId);

        public string GetRawString(string key, string? instanceId = null) => inner.GetRawString(key, instanceId);

        public SettingsValueSource GetSource(string key, string? instanceId = null) => inner.GetSource(key, instanceId);

        public bool IsVisible(OptionDefinition definition, string? instanceId = null) =>
            inner.IsVisible(definition, instanceId);

        public bool TrySetRaw(string key, string? rawValue, string? instanceId, out string? error)
        {
            if (string.Equals(key, "launcher.uiScale", StringComparison.Ordinal))
            {
                error = "磁盘只读";
                return false;
            }

            return inner.TrySetRaw(key, rawValue, instanceId, out error);
        }

        public bool TryClear(string key, string? instanceId, out string? error) =>
            inner.TryClear(key, instanceId, out error);

        public event EventHandler<SettingsChangedEventArgs>? Changed
        {
            add => inner.Changed += value;
            remove => inner.Changed -= value;
        }
    }

    private sealed class FakeDialogService : IDialogService
    {
        public bool ConfirmResult { get; set; } = true;

        public bool Confirm(string title, string message, string? acknowledgementToken = null) => ConfirmResult;

        public InstanceDeletionChoice ChooseInstanceDeletion(string title, string message, bool canDeleteManagedData) =>
            InstanceDeletionChoice.Cancel;

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
