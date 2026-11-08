// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.IO;
using Ecl.App.Services;
using Ecl.App.ViewModels;
using Ecl.App.ViewModels.Settings;
using Ecl.Configuration;
using Ecl.Configuration.Settings;
using Ecl.Core;
using Ecl.Deployment;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecl.App.Tests.Settings;

/// <summary>
/// 设置中心（B3 步骤 1，settings.md §3/§4/§7）：
/// 8 个固定分类与默认页、不可用分类不建空页、元数据驱动、即时保存与非法值拒绝、
/// 按分类恢复默认值、设置搜索定位，以及“设置中心不再编辑实例作用域”。
/// </summary>
public sealed class SettingsPageTests
{
    [Fact]
    public void NavigationHasTheEightApprovedCategoriesAndDefaultsToGeneral()
    {
        using var host = new SettingsHost();

        var keys = host.ViewModel.Navigation.Items.Select(item => item.Key).ToArray();

        Assert.Equal(
            [
                SettingsPageKeys.General,
                SettingsPageKeys.Interface,
                SettingsPageKeys.Java,
                SettingsPageKeys.GameLaunch,
                SettingsPageKeys.Downloads,
                SettingsPageKeys.Network,
                SettingsPageKeys.Storage,
                SettingsPageKeys.Additions,
            ],
            keys);
        Assert.Equal(SettingsPageKeys.General, host.ViewModel.Navigation.SelectedItem!.Key);
        Assert.Equal("常规", host.ViewModel.Navigation.CurrentPage!.Title);
        Assert.Equal(
            [
                SettingsPageKeys.General,
                SettingsPageKeys.Interface,
                SettingsPageKeys.Java,
                SettingsPageKeys.GameLaunch,
                SettingsPageKeys.Downloads,
                SettingsPageKeys.Network,
            ],
            host.ViewModel.Navigation.Items.Where(item => item.IsAvailable).Select(item => item.Key));
    }

    [Fact]
    public void UnimplementedCategoriesStayInTheNavigationWithAReasonAndCannotBeSelected()
    {
        using var host = new SettingsHost();

        foreach (var key in new[] { SettingsPageKeys.Storage, SettingsPageKeys.Additions })
        {
            var item = host.ViewModel.Navigation.Find(key)!;
            Assert.False(item.IsAvailable);
            Assert.Null(item.Page);
            Assert.False(string.IsNullOrWhiteSpace(item.UnavailableReason));

            host.ViewModel.Navigation.SelectedItem = item;
            Assert.Equal(SettingsPageKeys.General, host.ViewModel.Navigation.SelectedItem!.Key);
            Assert.False(host.ViewModel.Navigation.TryActivate(key));
        }
    }

    [Fact]
    public void SelectingACategoryKeepsItUntilSettingsIsEnteredAgain()
    {
        using var host = new SettingsHost();

        Assert.True(host.ViewModel.Navigation.TryActivate(SettingsPageKeys.Network));
        Assert.Equal(SettingsPageKeys.Network, host.ViewModel.Navigation.SelectedItem!.Key);

        // 页面内切换分类保留当前会话选择。
        Assert.True(host.ViewModel.Navigation.TryActivate(SettingsPageKeys.GameLaunch));
        Assert.Equal(SettingsPageKeys.GameLaunch, host.ViewModel.Navigation.SelectedItem!.Key);

        // 重新进入设置中心回到常规（settings.md §2）。
        host.ViewModel.Activate();
        Assert.Equal(SettingsPageKeys.General, host.ViewModel.Navigation.SelectedItem!.Key);
    }

    [Fact]
    public void GeneralPageOnlyRendersGlobalMetadataOptionsAndNeverInstanceScope()
    {
        using var host = new SettingsHost();
        var general = Page(host, SettingsPageKeys.General);

        Assert.Equal(
            ["launcher.checkUpdates", "launcher.updateChannel", "launcher.confirmBeforeDelete"],
            general.Options.Select(option => option.Key));
        Assert.All(general.Options, option => Assert.Equal(OptionScope.Global, option.Definition.Scope));
        Assert.DoesNotContain(
            host.ViewModel.Navigation.Items.SelectMany(item => item.Page?.SearchEntries ?? []),
            entry => entry.OptionKey.StartsWith("instance.", StringComparison.Ordinal));
    }

    [Fact]
    public void SettingsNoLongerTakesAnInstanceServiceOrScopeSwitch()
    {
        var parameters = typeof(SettingsViewModel)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToArray();

        Assert.DoesNotContain(typeof(Ecl.Minecraft.Instances.IInstanceService), parameters);
        Assert.DoesNotContain(typeof(Ecl.Java.IJavaLocator), parameters);
    }

    [Fact]
    public void UpdateChannelShowsSpecNamesWhilePersistingExistingValues()
    {
        using var host = new SettingsHost();
        var general = Page(host, SettingsPageKeys.General);
        var channel = general.Options.Single(option => option.Key == "launcher.updateChannel");

        Assert.Equal(["main", "dev", "nightly"], channel.Choices.Select(choice => choice.Value));
        Assert.Equal(["Stable", "Beta", "Nightly"], channel.Choices.Select(choice => choice.DisplayName));
        Assert.Equal("main", channel.TextValue);

        channel.TextValue = "dev";

        Assert.Equal("dev", host.Settings.GetRawString("launcher.updateChannel"));
        Assert.Equal(SettingsValueSource.Global, host.Settings.GetSource("launcher.updateChannel"));
    }

    [Fact]
    public void LegacyUpdateChannelAliasDisplaysAsStableAndIsRewrittenOnlyWhenChanged()
    {
        using var host = new SettingsHost(
            seedSettingsFile: file => File.WriteAllText(
                file,
                "{\"schemaVersion\":1,\"values\":{\"launcher.updateChannel\":\"stable\"}}"));
        var channel = Page(host, SettingsPageKeys.General)
            .Options.Single(option => option.Key == "launcher.updateChannel");

        // D-01：旧值 stable 是 main 的别名；界面按规范项显示且不算未保存修改。
        Assert.Equal("stable", channel.TextValue);
        Assert.Equal("Stable", channel.SelectedChoice!.DisplayName);
        Assert.False(channel.IsDirty);
        Assert.Equal("stable", host.Settings.GetRawString("launcher.updateChannel"));

        channel.SelectedChoice = channel.Choices.Single(choice => choice.Value == "dev");

        Assert.Equal("dev", host.Settings.GetRawString("launcher.updateChannel"));
        Assert.Equal(SettingsValueSource.Global, host.Settings.GetSource("launcher.updateChannel"));
    }

    [Fact]
    public void BooleanOptionsSaveImmediatelyAndInvalidManualInputIsRejected()
    {
        using var host = new SettingsHost();
        var general = Page(host, SettingsPageKeys.General);
        var checkUpdates = general.Options.Single(option => option.Key == "launcher.checkUpdates");

        Assert.True(checkUpdates.BoolValue);
        checkUpdates.BoolValue = false;

        Assert.Equal("false", host.Settings.GetRawString("launcher.checkUpdates"));
        Assert.Equal(SettingsValueSource.Global, host.Settings.GetSource("launcher.checkUpdates"));

        var game = Page(host, SettingsPageKeys.GameLaunch);
        var memory = game.Options.Single(option => option.Key == "java.defaultMemoryMb");
        memory.TextValue = "not-a-number";
        memory.CommitCommand.Execute(null);

        Assert.True(memory.HasError);
        Assert.False(string.IsNullOrWhiteSpace(memory.ErrorText));
        Assert.Equal(SettingsValueSource.Default, host.Settings.GetSource("java.defaultMemoryMb"));
        Assert.Equal("4096", host.Settings.GetRawString("java.defaultMemoryMb"));

        memory.TextValue = "8192";
        memory.CommitCommand.Execute(null);

        Assert.False(memory.HasError);
        Assert.False(memory.IsDirty);
        Assert.Equal(8192, host.Settings.Get<int>("java.defaultMemoryMb"));
        Assert.Equal(SettingsValueSource.Global, host.Settings.GetSource("java.defaultMemoryMb"));
    }

    [Fact]
    public void DependentOptionVisibilityFollowsTheSavedValue()
    {
        using var host = new SettingsHost();
        var general = Page(host, SettingsPageKeys.General);
        var checkUpdates = general.Options.Single(option => option.Key == "launcher.checkUpdates");
        var channel = general.Options.Single(option => option.Key == "launcher.updateChannel");

        Assert.True(channel.IsVisible);

        checkUpdates.BoolValue = false;

        Assert.False(channel.IsVisible);

        checkUpdates.BoolValue = true;

        Assert.True(channel.IsVisible);
    }

    [Fact]
    public void ResettingACategoryOnlyClearsThatCategoryAfterConfirmation()
    {
        using var host = new SettingsHost();
        host.Settings.TrySetRaw("launcher.checkUpdates", "false", null, out _);
        host.Settings.TrySetRaw("launcher.confirmBeforeDelete", "false", null, out _);
        host.Settings.TrySetRaw("launcher.language", "en-US", null, out _);
        var general = Page(host, SettingsPageKeys.General);

        // 取消确认时不改变任何值。
        host.Dialogs.ConfirmResult = false;
        general.ResetToDefaults();

        Assert.Equal("false", host.Settings.GetRawString("launcher.checkUpdates"));
        Assert.Equal("false", host.Settings.GetRawString("launcher.confirmBeforeDelete"));
        Assert.Equal("en-US", host.Settings.GetRawString("launcher.language"));

        host.Dialogs.ConfirmResult = true;
        general.ResetToDefaults();

        Assert.Equal("true", host.Settings.GetRawString("launcher.checkUpdates"));
        Assert.Equal(SettingsValueSource.Default, host.Settings.GetSource("launcher.checkUpdates"));
        Assert.Equal(SettingsValueSource.Default, host.Settings.GetSource("launcher.confirmBeforeDelete"));
        // 其它分类不受影响。
        Assert.Equal("en-US", host.Settings.GetRawString("launcher.language"));
        Assert.Equal(SettingsValueSource.Global, host.Settings.GetSource("launcher.language"));
        Assert.False(string.IsNullOrWhiteSpace(general.StatusMessage));
    }

    [Fact]
    public void SearchFindsNamesAndDescriptionsAndLocatesTheSetting()
    {
        using var host = new SettingsHost();
        var viewModel = host.ViewModel;

        viewModel.SearchText = "更新";

        Assert.True(viewModel.IsSearching);
        Assert.True(viewModel.HasSearchResults);
        Assert.Contains(viewModel.SearchResults, result => result.OptionKey == "launcher.checkUpdates");
        Assert.Contains(viewModel.SearchResults, result => result.OptionKey == "launcher.updateChannel");
        Assert.All(
            viewModel.SearchResults,
            result => Assert.Contains(
                host.ViewModel.Navigation.Items.Where(item => item.IsAvailable).Select(item => item.Key),
                key => key == result.PageKey));

        viewModel.SearchText = "不存在的设置项";
        Assert.True(viewModel.HasNoSearchResults);
        Assert.Empty(viewModel.SearchResults);

        viewModel.SearchText = "代理地址";
        var result = Assert.Single(
            viewModel.SearchResults,
            item => item.OptionKey == "network.proxyAddress");
        viewModel.OpenSearchResultCommand.Execute(result);

        Assert.False(viewModel.IsSearching);
        Assert.Equal(SettingsPageKeys.Network, viewModel.Navigation.SelectedItem!.Key);
        var network = Page(host, SettingsPageKeys.Network);
        var proxyMode = network.Options.Single(option => option.Key == "network.proxyMode");
        var proxyAddress = network.Options.Single(option => option.Key == "network.proxyAddress");

        // 目标项被 VisibleWhen 隐藏时，定位到控制它的选项并说明解锁条件。
        Assert.False(proxyAddress.IsVisible);
        Assert.True(proxyMode.IsHighlighted);
        Assert.False(proxyAddress.IsHighlighted);
        Assert.Contains("仅在", network.StatusMessage);

        // 满足可见条件后，再次定位到具体设置项。
        proxyMode.TextValue = "manual";
        viewModel.SearchText = "代理地址";
        viewModel.OpenSearchResultCommand.Execute(
            Assert.Single(viewModel.SearchResults, item => item.OptionKey == "network.proxyAddress"));

        Assert.True(proxyAddress.IsVisible);
        Assert.True(proxyAddress.IsHighlighted);
        Assert.Contains("已定位到", network.StatusMessage);
    }

    [Fact]
    public void EveryImplementedPageDeclaresItsCapabilityGapsInsteadOfFakeControls()
    {
        using var host = new SettingsHost();

        foreach (var key in new[]
                 {
                     SettingsPageKeys.General,
                     SettingsPageKeys.Interface,
                     SettingsPageKeys.Java,
                     SettingsPageKeys.GameLaunch,
                     SettingsPageKeys.Downloads,
                     SettingsPageKeys.Network,
                 })
        {
            var page = Page(host, key);
            var gaps = page.Sections.OfType<SettingsUnavailableSectionViewModel>().ToArray();
            Assert.NotEmpty(gaps);
            Assert.All(
                gaps.SelectMany(gap => gap.Items),
                item =>
                {
                    Assert.False(string.IsNullOrWhiteSpace(item.Title));
                    Assert.False(string.IsNullOrWhiteSpace(item.Reason));
                });
        }

        var network = Page(host, SettingsPageKeys.Network);
        Assert.Equal(
            ["network.proxyMode", "network.proxyAddress"],
            network.Options.Select(option => option.Key));
    }

    [Fact]
    public void JavaPageKeepsTheLivePathAndProviderOptionsWhileTheInventoryIsPending()
    {
        using var host = new SettingsHost();
        var java = Page(host, SettingsPageKeys.Java);

        Assert.Equal(["java.defaultPath", "java.runtimeProvider"], java.Options.Select(option => option.Key));
        var gap = Assert.Single(java.Sections.OfType<SettingsUnavailableSectionViewModel>());
        Assert.Contains(gap.Items, item => item.Title.Contains("运行时清单", StringComparison.Ordinal));
    }

    [Fact]
    public void CodeSigningTrustCardBelongsToTheGeneralPageOnly()
    {
        using var host = new SettingsHost();

        Assert.Single(Page(host, SettingsPageKeys.General).Sections.OfType<CodeSigningSectionViewModel>());
        Assert.Empty(Page(host, SettingsPageKeys.Interface).Sections.OfType<CodeSigningSectionViewModel>());
    }

    private static SettingsOptionsPageViewModel Page(SettingsHost host, string key) =>
        Assert.IsAssignableFrom<SettingsOptionsPageViewModel>(host.ViewModel.Navigation.Find(key)!.Page);

    private sealed class SettingsHost : IDisposable
    {
        private readonly string _root;
        private readonly ServiceProvider _provider;

        public SettingsHost(
            Action<ISettingsService>? configure = null,
            Action<string>? seedSettingsFile = null)
        {
            _root = Path.Combine(Path.GetTempPath(), "ecl-settings-tests", Guid.NewGuid().ToString("N"));
            var paths = new AppPaths(_root);
            paths.EnsureCreated();
            seedSettingsFile?.Invoke(paths.GlobalSettingsFile);

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddEclCore(paths, CultureInfo.GetCultureInfo("zh-CN"));
            services.AddEclConfiguration();
            _provider = services.BuildServiceProvider();

            Settings = _provider.GetRequiredService<ISettingsService>();
            configure?.Invoke(Settings);
            Dialogs = new FakeDialogService();
            ViewModel = new SettingsViewModel(
                Settings,
                _provider.GetRequiredService<Ecl.Core.Localization.ILocalizer>(),
                new CodeSigningTrustService(Path.Combine(_root, "signing")),
                Dialogs);
        }

        public ISettingsService Settings { get; }

        public FakeDialogService Dialogs { get; }

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
