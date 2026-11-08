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
using Ecl.Core.Diagnostics;
using Ecl.Deployment;
using Ecl.Java;
using Ecl.Minecraft.Launch;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ecl.App.Tests.Settings;

/// <summary>
/// 设置中心「游戏与启动」与「下载」页（B3 步骤 4）：
/// D-03 内存策略与真实内存呈现、窗口宽高联合校验、默认全屏，
/// 以及下载重试次数/开关与限速的真实配置入口。
/// </summary>
public sealed class GameLaunchAndDownloadsPageTests
{
    [Fact]
    public void GamePageRendersFourGroupsAndTheRealMemoryPlan()
    {
        using var host = new Host();
        var page = host.GamePage;

        Assert.Equal(
            [
                "java.memoryPolicy",
                "java.defaultMemoryMb",
                "java.extraArguments",
                "game.windowWidth",
                "game.windowHeight",
                "game.fullscreen",
                "game.showConsole",
                "game.extraArguments",
            ],
            page.Options.Select(option => option.Key));
        Assert.Equal(
            ["java.memoryPolicy", "java.defaultMemoryMb"],
            page.MemoryGroup.Options.Select(option => option.Key));
        Assert.Equal(["java.extraArguments"], page.JvmGroup.Options.Select(option => option.Key));
        Assert.Equal(
            ["game.showConsole", "game.extraArguments"],
            page.LaunchGroup.Options.Select(option => option.Key));
        Assert.NotEmpty(page.Boundaries.Items);

        // 默认策略为自动；手动数值保留在元数据中但当前不可见。
        var policy = page.MemoryGroup.Options.Single(option => option.Key == "java.memoryPolicy");
        var manual = page.MemoryGroup.Options.Single(option => option.Key == "java.defaultMemoryMb");
        Assert.Equal("auto", policy.TextValue);
        Assert.False(manual.IsVisible);
        Assert.Equal(SettingsValueSource.Default, host.Settings.GetSource("java.memoryPolicy"));

        // 16 GB 可用内存对应自动分配 8192 MB；页面显示的是启动服务的领域结果。
        Assert.Contains("32,768 MB", page.MemoryTotalText);
        Assert.Contains("16,384 MB", page.MemoryAvailableText);
        Assert.Contains("8,192 MB", page.MemoryAllocationText);
        Assert.False(page.HasMemoryWarning);
    }

    [Fact]
    public void SwitchingToManualUsesAndPersistsTheManualValue()
    {
        using var host = new Host(availableMegabytes: 8000);
        var page = host.GamePage;
        var policy = page.MemoryGroup.Options.Single(option => option.Key == "java.memoryPolicy");
        var manual = page.MemoryGroup.Options.Single(option => option.Key == "java.defaultMemoryMb");

        // 8 GB 可用内存自动分配 3072 MB。
        Assert.Contains("3,072 MB", page.MemoryAllocationText);

        policy.SelectedChoice = policy.Choices.Single(choice => choice.Value == "manual");

        Assert.Equal("manual", host.Settings.GetRawString("java.memoryPolicy"));
        Assert.True(manual.IsVisible);
        Assert.Contains("4,096 MB", page.MemoryAllocationText);

        manual.TextValue = "8192";
        manual.CommitCommand.Execute(null);

        Assert.Equal(8192, host.Settings.Get<int>("java.defaultMemoryMb"));
        Assert.Equal(SettingsValueSource.Global, host.Settings.GetSource("java.defaultMemoryMb"));
        Assert.Contains("8,192 MB", page.MemoryAllocationText);

        // 切回自动：手动数值保留，但不再生效，行随之隐藏。
        policy.SelectedChoice = policy.Choices.Single(choice => choice.Value == "auto");

        Assert.Equal(8192, host.Settings.Get<int>("java.defaultMemoryMb"));
        Assert.False(manual.IsVisible);
        Assert.Contains("3,072 MB", page.MemoryAllocationText);
    }

    [Fact]
    public void LegacyExplicitMemoryWithoutPolicyIsTreatedAsManual()
    {
        using var host = new Host(seedSettings: settings =>
            settings.TrySetRaw("java.defaultMemoryMb", "4096", null, out _));
        var page = host.GamePage;

        // 迁移尚未运行或迁移失败时，运行时的兼容回退仍把显式数值当作手动值。
        Assert.Equal(SettingsValueSource.Default, host.Settings.GetSource("java.memoryPolicy"));
        Assert.Contains("4,096 MB", page.MemoryAllocationText);
        var manual = page.MemoryGroup.Options.Single(option => option.Key == "java.defaultMemoryMb");
        Assert.True(manual.IsVisible);
    }

    [Fact]
    public void ManualValueAboveAvailableMemoryShowsPressureWarning()
    {
        using var host = new Host(availableMegabytes: 4 * 1024);
        var page = host.GamePage;
        var policy = page.MemoryGroup.Options.Single(option => option.Key == "java.memoryPolicy");
        var manual = page.MemoryGroup.Options.Single(option => option.Key == "java.defaultMemoryMb");

        policy.SelectedChoice = policy.Choices.Single(choice => choice.Value == "manual");
        manual.TextValue = "16384";
        manual.CommitCommand.Execute(null);

        Assert.True(page.HasMemoryWarning);
        Assert.Contains("16,384 MB", page.MemoryWarning);
        Assert.Contains("4,096 MB", page.MemoryWarning);
    }

    [Fact]
    public void AutomaticPolicyFallsBackHonestlyWhenSystemMemoryIsUnavailable()
    {
        using var host = new Host(probe: null);
        var page = host.GamePage;

        Assert.Equal("不可用", page.MemoryTotalText);
        Assert.Equal("不可用", page.MemoryAvailableText);
        Assert.Contains("4,096 MB", page.MemoryAllocationText);
        Assert.True(page.HasMemoryWarning);
        Assert.Contains("4,096 MB", page.MemoryWarning);
    }

    [Fact]
    public void WindowSizeIsValidatedAndSavedAsOneGroup()
    {
        using var host = new Host();
        var page = host.GamePage;

        Assert.Equal("1280", page.WindowWidthText);
        Assert.Equal("720", page.WindowHeightText);
        Assert.False(page.IsFullscreen);
        Assert.False(page.CanApplyWindow);

        // 宽度非法：不写任何一半的尺寸。
        page.WindowWidthText = "abc";
        Assert.True(page.CanApplyWindow);
        page.ApplyWindowCommand.Execute(null);

        Assert.True(page.HasWindowError);
        Assert.Equal(SettingsValueSource.Default, host.Settings.GetSource("game.windowWidth"));
        Assert.Equal(SettingsValueSource.Default, host.Settings.GetSource("game.windowHeight"));

        // 高度超出范围：宽度同样不保存。
        page.WindowWidthText = "1600";
        page.WindowHeightText = "100";
        page.ApplyWindowCommand.Execute(null);

        Assert.True(page.HasWindowError);
        Assert.Equal(SettingsValueSource.Default, host.Settings.GetSource("game.windowWidth"));

        page.WindowHeightText = "900";
        page.ApplyWindowCommand.Execute(null);

        Assert.False(page.HasWindowError);
        Assert.Equal(1600, host.Settings.Get<int>("game.windowWidth"));
        Assert.Equal(900, host.Settings.Get<int>("game.windowHeight"));
        Assert.False(page.CanApplyWindow);
        Assert.False(string.IsNullOrWhiteSpace(page.WindowStatus));

        // 默认全屏是独立即时保存的开关。
        page.IsFullscreen = true;
        Assert.Equal("true", host.Settings.GetRawString("game.fullscreen"));

        page.IsFullscreen = false;
        Assert.Equal("false", host.Settings.GetRawString("game.fullscreen"));

        // 还原只恢复未保存的编辑值，不写配置。
        page.WindowWidthText = "2000";
        page.RevertWindowCommand.Execute(null);
        Assert.Equal("1600", page.WindowWidthText);
        Assert.Equal(1600, host.Settings.Get<int>("game.windowWidth"));
    }

    [Fact]
    public void DownloadsPageExposesRetryPolicyAndSpeedLimit()
    {
        using var host = new Host();
        var downloads = Assert.IsAssignableFrom<SettingsOptionsPageViewModel>(
            host.ViewModel.Navigation.Find(SettingsPageKeys.Downloads)!.Page);

        Assert.Equal(
            [
                "network.downloadSource",
                "network.maxConcurrentDownloads",
                "network.speedLimitMbps",
                "network.autoRetry",
                "network.maxRetryAttempts",
            ],
            downloads.Options.Select(option => option.Key));

        var autoRetry = downloads.Options.Single(option => option.Key == "network.autoRetry");
        var maxAttempts = downloads.Options.Single(option => option.Key == "network.maxRetryAttempts");

        Assert.True(autoRetry.BoolValue);
        Assert.True(maxAttempts.IsVisible);
        Assert.Equal("3", maxAttempts.TextValue);

        autoRetry.BoolValue = false;
        Assert.Equal("false", host.Settings.GetRawString("network.autoRetry"));
        Assert.False(maxAttempts.IsVisible);

        autoRetry.BoolValue = true;
        Assert.True(maxAttempts.IsVisible);

        // 旧的“未接入”缺口不再出现，改为真实控件 + 新的边界说明。
        var gapTitles = downloads.Sections
            .OfType<SettingsUnavailableSectionViewModel>()
            .SelectMany(gap => gap.Items)
            .Select(item => item.Title)
            .ToArray();
        Assert.DoesNotContain("自动重试与最大重试次数", gapTitles);
        Assert.DoesNotContain("下载限速", gapTitles);
        Assert.Contains("关闭完整性校验", gapTitles);
    }

    [Fact]
    public void SearchFindsGameMemoryAndDownloadLimitAndLocatesThem()
    {
        using var host = new Host();
        var viewModel = host.ViewModel;

        viewModel.SearchText = "内存分配策略";
        var policyResult = Assert.Single(
            viewModel.SearchResults,
            result => result.OptionKey == "java.memoryPolicy");
        viewModel.OpenSearchResultCommand.Execute(policyResult);

        Assert.Equal(SettingsPageKeys.GameLaunch, viewModel.Navigation.SelectedItem!.Key);
        Assert.Equal("已定位到：内存分配策略", host.GamePage.StatusMessage);

        viewModel.SearchText = "下载限速";
        var limitResult = Assert.Single(
            viewModel.SearchResults,
            result => result.OptionKey == "network.speedLimitMbps");
        viewModel.OpenSearchResultCommand.Execute(limitResult);

        Assert.Equal(SettingsPageKeys.Downloads, viewModel.Navigation.SelectedItem!.Key);
        var downloads = Assert.IsAssignableFrom<SettingsOptionsPageViewModel>(
            viewModel.Navigation.CurrentPage);
        Assert.True(downloads.Options.Single(option => option.Key == "network.speedLimitMbps").IsHighlighted);
    }

    [Fact]
    public void ResetCurrentPageWorksForDedicatedGamePage()
    {
        using var host = new Host();
        host.Settings.TrySetRaw("java.memoryPolicy", "manual", null, out _);
        host.Settings.TrySetRaw("java.defaultMemoryMb", "8192", null, out _);
        host.Settings.TrySetRaw("game.windowWidth", "1600", null, out _);
        host.Settings.TrySetRaw("launcher.checkUpdates", "false", null, out _);

        Assert.True(host.ViewModel.Navigation.TryActivate(SettingsPageKeys.GameLaunch));
        host.Dialogs.ConfirmResult = true;
        host.ViewModel.ResetCurrentPageCommand.Execute(null);

        Assert.Equal(SettingsValueSource.Default, host.Settings.GetSource("java.memoryPolicy"));
        Assert.Equal(SettingsValueSource.Default, host.Settings.GetSource("java.defaultMemoryMb"));
        Assert.Equal(SettingsValueSource.Default, host.Settings.GetSource("game.windowWidth"));
        // 其它分类不受影响。
        Assert.Equal("false", host.Settings.GetRawString("launcher.checkUpdates"));
    }

    private sealed class Host : IDisposable
    {
        private readonly string _root;
        private readonly ServiceProvider _provider;

        public Host(
            long totalMegabytes = 32 * 1024,
            long availableMegabytes = 16 * 1024,
            bool? probe = true,
            Action<ISettingsService>? seedSettings = null)
        {
            _root = Path.Combine(Path.GetTempPath(), "ecl-game-tests", Guid.NewGuid().ToString("N"));
            var paths = new AppPaths(_root);
            paths.EnsureCreated();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddEclCore(paths, CultureInfo.GetCultureInfo("zh-CN"));
            services.AddEclConfiguration();
            _provider = services.BuildServiceProvider();

            Settings = _provider.GetRequiredService<ISettingsService>();
            seedSettings?.Invoke(Settings);
            Dialogs = new FakeDialogService();
            var localizer = _provider.GetRequiredService<Ecl.Core.Localization.ILocalizer>();
            var javaPage = new SettingsJavaPageViewModel(
                new FakeJavaLocator(),
                new JavaRuntimeRegistry(paths, NullLogger<JavaRuntimeRegistry>.Instance),
                new FakeJavaRuntimeValidator(),
                new FakeJavaRuntimeUninstallService(),
                Settings,
                Dialogs,
                paths,
                localizer,
                NullLogger<SettingsJavaPageViewModel>.Instance);

            ViewModel = new SettingsViewModel(
                Settings,
                localizer,
                new CodeSigningTrustService(Path.Combine(_root, "signing")),
                Dialogs,
                javaPage,
                new MemoryAllocationService(
                    Settings,
                    probe == true
                        ? new StubSystemMemoryProbe(totalMegabytes, availableMegabytes)
                        : new NullSystemMemoryProbe()));

            GamePage = Assert.IsType<SettingsGameLaunchPageViewModel>(
                ViewModel.Navigation.Find(SettingsPageKeys.GameLaunch)!.Page);
        }

        public ISettingsService Settings { get; }

        public FakeDialogService Dialogs { get; }

        public SettingsViewModel ViewModel { get; }

        public SettingsGameLaunchPageViewModel GamePage { get; }

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

    private sealed class StubSystemMemoryProbe(long totalMegabytes, long availableMegabytes)
        : ISystemMemoryProbe
    {
        public MemoryPressureSample? Read() => new(
            availableMegabytes * 1024 * 1024,
            totalMegabytes * 1024 * 1024,
            0,
            0);
    }

    private sealed class NullSystemMemoryProbe : ISystemMemoryProbe
    {
        public MemoryPressureSample? Read() => null;
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
