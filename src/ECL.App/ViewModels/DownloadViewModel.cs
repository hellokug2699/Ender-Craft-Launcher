// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.Core.Localization;
using Ecl.Minecraft.Content;

namespace Ecl.App.ViewModels;

/// <summary>
/// “下载”页（下载中心）：横向二级目录固定为实例版本、模组、整合包、资源包、光影包、数据包
/// （`download-instance-versions.md` §1）。
/// <para>
/// 实例版本页由 <see cref="InstanceVersion"/> 独立持有页面状态；其余五个分类由各自的内容发现页
/// （<see cref="ContentPages"/>）承载，切换二级页只清空该页的旧结果，不影响其它分类的已配置状态。
/// </para>
/// <para>
/// 实例安装只在这里的新建流程中发生（`InstancesViewModel.InstallCommand` 的旧过渡入口已移除）；
/// 既有实例的补全/修复属于实例工作区的「补全/修复」入口。
/// </para>
/// </summary>
public sealed partial class DownloadViewModel : ObservableObject
{
    private readonly ILocalizer _localizer;
    private bool _initialized;

    public InstanceVersionPageViewModel InstanceVersion { get; }

    /// <summary>内容发现页按二级目录顺序持有：实例版本之外的五个分类。</summary>
    public IReadOnlyList<ContentDiscoveryViewModel> ContentPages { get; }

    public ObservableCollection<DownloadTabViewModel> Tabs { get; } = [];

    [ObservableProperty]
    private DownloadTabViewModel? _selectedTab;

    public bool IsGameTab => SelectedTab?.Kind is null;

    public bool IsContentTab => !IsGameTab;

    public ContentDiscoveryViewModel? SelectedContentPage => SelectedTab?.Kind is { } kind
        ? ContentPages.FirstOrDefault(page => page.Kind == kind)
        : null;

    public DownloadViewModel(
        InstanceVersionPageViewModel instanceVersion,
        ContentDiscoveryPageFactory pageFactory,
        ILocalizer localizer)
    {
        _localizer = localizer;
        InstanceVersion = instanceVersion;

        // 顺序固定（download-instance-versions.md §1）：实例版本、模组、整合包、资源包、光影包、数据包。
        foreach (var tab in DownloadCategories.Create(localizer))
        {
            Tabs.Add(tab);
        }

        _selectedTab = Tabs[0];
        ContentPages = pageFactory.CreateAll();

        // 整合包版本只负责发现；选定版本后统一进入实例版本页的整合包模式（download-modpacks.md §8）。
        foreach (var page in ContentPages.Where(page => page.Kind == ContentKind.ModPack))
        {
            page.OpenModpackDeployment = OpenModpackDeploymentAsync;
        }
    }

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        await InstanceVersion.EnsureLoadedAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// 从实例工作区的模组/资源包/光影包页打开本页（`download-mods.md` §4、
    /// `download-resource-template.md` §5）：按结构化上下文切换到对应内容页，
    /// 已有实例模式带入稳定 InstanceId 与该实例的版本/加载器，再用给定搜索词检索。
    /// </summary>
    public async Task OpenContentBrowserAsync(ContentSearchContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var page = ContentPages.FirstOrDefault(item => item.Kind == context.Kind);
        if (page is null)
        {
            return;
        }

        SelectedTab = Tabs.FirstOrDefault(tab => tab.Kind == context.Kind) ?? Tabs[0];
        await page.ActivateAsync(context).ConfigureAwait(true);
    }

    /// <summary>
    /// 在线整合包版本 → 实例版本页整合包模式（`download-modpacks.md` §8）。
    /// 归档下载与清单解析由实例版本页负责，部署仍走统一的新建实例流程。
    /// </summary>
    public async Task OpenModpackDeploymentAsync(ContentHit hit, ContentVersion version)
    {
        ArgumentNullException.ThrowIfNull(hit);
        ArgumentNullException.ThrowIfNull(version);

        SelectedTab = Tabs[0];
        await InstanceVersion
            .EnterModpackModeAsync(ModpackDeploymentSource.FromOnline(hit, version))
            .ConfigureAwait(true);
    }

    /// <summary>
    /// 本地导入整合包 → 实例版本页整合包模式（`instances.md` §5.4）。主动选择与全局拖拽共用本入口。
    /// </summary>
    public async Task OpenModpackDeploymentAsync(string archivePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);

        SelectedTab = Tabs[0];
        await InstanceVersion
            .EnterModpackModeAsync(ModpackDeploymentSource.FromLocalArchive(archivePath))
            .ConfigureAwait(true);
    }

    /// <summary>每次进入下载目的地时刷新目标实例与当前分类，不重置已配置的筛选（§14）。</summary>
    public async Task ActivateAsync()
    {
        if (!_initialized)
        {
            return;
        }

        await InstanceVersion.RefreshAsync().ConfigureAwait(true);
        if (SelectedContentPage is { } page)
        {
            await page.ActivateAsync().ConfigureAwait(true);
        }
    }

    /// <summary>顶部「刷新」：只刷新当前可见页面的真实数据，不重置已配置的选择。</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        await InstanceVersion.RefreshAsync().ConfigureAwait(true);
        if (SelectedContentPage is { } page)
        {
            await page.ActivateAsync().ConfigureAwait(true);
        }
    }

    partial void OnSelectedTabChanged(DownloadTabViewModel? value)
    {
        OnPropertyChanged(nameof(IsGameTab));
        OnPropertyChanged(nameof(IsContentTab));
        OnPropertyChanged(nameof(SelectedContentPage));

        if (_initialized && SelectedContentPage is { } page)
        {
            _ = page.ActivateAsync();
        }
    }
}

public sealed record DownloadTabViewModel(string Key, ContentKind? Kind, string Title)
{
    /// <summary>UIA/无障碍名称只使用本地化标题，不暴露记录类型的 ToString()。</summary>
    public override string ToString() => Title;
}

/// <summary>
/// 下载中心横向二级目录的唯一构造点（`download-instance-versions.md` §1）：
/// 分类与顺序固定为实例版本、模组、整合包、资源包、光影包、数据包。
/// </summary>
public static class DownloadCategories
{
    public static IReadOnlyList<DownloadTabViewModel> Create(ILocalizer localizer) =>
    [
        new DownloadTabViewModel("version", null, localizer.Get("downloads.tab.version")),
        new DownloadTabViewModel("mod", ContentKind.Mod, localizer.Get("downloads.tab.mod")),
        new DownloadTabViewModel("modpack", ContentKind.ModPack, localizer.Get("downloads.kind.modpack")),
        new DownloadTabViewModel("resourcepack", ContentKind.ResourcePack, localizer.Get("downloads.kind.resourcepack")),
        new DownloadTabViewModel("shader", ContentKind.ShaderPack, localizer.Get("downloads.kind.shaderpack")),
        new DownloadTabViewModel("datapack", ContentKind.DataPack, localizer.Get("downloads.kind.datapack")),
    ];
}
