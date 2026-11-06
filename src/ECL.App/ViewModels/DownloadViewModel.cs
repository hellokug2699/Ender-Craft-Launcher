// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.Core.Localization;
using Ecl.Minecraft.Content;
using Ecl.Minecraft.Instances;

namespace Ecl.App.ViewModels;

/// <summary>
/// “下载”页（下载中心）：横向二级目录固定为实例版本、模组、整合包、资源包、光影包、数据包
/// （`download-instance-versions.md` §1）。
/// <para>
/// 实例版本页由 <see cref="InstanceVersion"/> 独立持有页面状态；其余五个分类由各自的内容发现页
/// （<see cref="ContentPages"/>）承载，切换二级页只清空该页的旧结果，不影响其它分类的已配置状态。
/// </para>
/// </summary>
public sealed partial class DownloadViewModel : ObservableObject
{
    private readonly IInstanceService _instances;
    private readonly ILocalizer _localizer;
    private bool _initialized;
    private bool _suppressInstanceFilter;

    public InstancesViewModel Game { get; }

    public InstanceVersionPageViewModel InstanceVersion { get; }

    /// <summary>内容发现页按二级目录顺序持有：实例版本之外的五个分类。</summary>
    public IReadOnlyList<ContentDiscoveryViewModel> ContentPages { get; }

    public ObservableCollection<DownloadTabViewModel> Tabs { get; } = [];

    public ObservableCollection<ContentInstanceChoice> TargetInstances { get; } = [];

    [ObservableProperty]
    private DownloadTabViewModel? _selectedTab;

    [ObservableProperty]
    private ContentInstanceChoice? _selectedInstance;

    public bool IsGameTab => SelectedTab?.Kind is null;

    public bool IsContentTab => !IsGameTab;

    public ContentDiscoveryViewModel? SelectedContentPage => SelectedTab?.Kind is { } kind
        ? ContentPages.FirstOrDefault(page => page.Kind == kind)
        : null;

    public bool HasTargetInstance => SelectedInstance?.Id is not null;

    public string TargetInstanceHint => SelectedInstance?.Id is null
        ? _localizer.Get("downloads.noInstanceHint")
        : _localizer.Get("downloads.instanceHint", SelectedInstance.Name);

    public DownloadViewModel(
        IInstanceService instances,
        InstancesViewModel game,
        InstanceVersionPageViewModel instanceVersion,
        ContentDiscoveryPageFactory pageFactory,
        ILocalizer localizer)
    {
        _instances = instances;
        _localizer = localizer;
        Game = game;
        InstanceVersion = instanceVersion;

        // 顺序固定（download-instance-versions.md §1）：实例版本、模组、整合包、资源包、光影包、数据包。
        foreach (var tab in DownloadCategories.Create(localizer))
        {
            Tabs.Add(tab);
        }

        _selectedTab = Tabs[0];
        ContentPages = pageFactory.CreateAll();
    }

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        await RefreshInstancesAsync().ConfigureAwait(true);
        await InstanceVersion.EnsureLoadedAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// 从实例工作区模组页打开本页（download-mods.md §4）：按已有实例目标模式带入稳定 InstanceId，
    /// 自动填充实例的 Minecraft 版本与加载器，再用给定搜索词检索（空搜索词表示直接浏览）。
    /// 目标实例已被移除或失效时明确要求重新选择，不静默退回自定义筛选/任意目录。
    /// </summary>
    public Task OpenModBrowserAsync(string instanceId, string? query) =>
        OpenContentBrowserAsync(instanceId, ContentKind.Mod, query);

    /// <summary>
    /// 从实例工作区的资源包/光影包页打开本页（resource-library-template.md §5「下载 xxx」）：
    /// 切到对应内容页签，并按已有实例目标模式带入稳定 InstanceId 与该实例的版本/加载器。
    /// </summary>
    public async Task OpenContentBrowserAsync(string instanceId, ContentKind kind, string? query)
    {
        await RefreshInstancesAsync().ConfigureAwait(true);

        var page = ContentPages.FirstOrDefault(item => item.Kind == kind);
        if (page is null)
        {
            return;
        }

        SelectedTab = Tabs.FirstOrDefault(tab => tab.Kind == kind) ?? Tabs[0];
        await page.ActivateForInstanceAsync(instanceId, query).ConfigureAwait(true);
    }

    /// <summary>每次进入下载目的地时刷新目标实例与当前分类的实例列表，不重置已配置的筛选。</summary>
    public async Task ActivateAsync()
    {
        if (!_initialized)
        {
            return;
        }

        await RefreshInstancesAsync().ConfigureAwait(true);
        await InstanceVersion.RefreshAsync().ConfigureAwait(true);
        if (SelectedContentPage is { } page)
        {
            await page.ActivateAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task RefreshInstancesAsync()
    {
        var selectedId = SelectedInstance?.Id;
        var instances = await _instances.GetInstancesAsync().ConfigureAwait(true);

        _suppressInstanceFilter = true;
        try
        {
            TargetInstances.Clear();
            TargetInstances.Add(new ContentInstanceChoice(null, _localizer.Get("downloads.noInstance"), string.Empty, string.Empty));
            foreach (var instance in instances)
            {
                TargetInstances.Add(new ContentInstanceChoice(
                    instance.Id,
                    instance.Name,
                    instance.GameVersion,
                    instance.Loader));
            }

            SelectedInstance = TargetInstances.FirstOrDefault(item => item.Id == selectedId) ?? TargetInstances[0];
        }
        finally
        {
            _suppressInstanceFilter = false;
        }

        OnPropertyChanged(nameof(TargetInstanceHint));
        OnPropertyChanged(nameof(HasTargetInstance));
    }

    [RelayCommand]
    private async Task InstallGameAsync()
    {
        if (SelectedInstance?.Id is not { } instanceId)
        {
            return;
        }

        Game.SelectedInstance = Game.Instances.FirstOrDefault(item => item.Model.Id == instanceId)
                                ?? Game.SelectedInstance;
        await Game.InstallCommand.ExecuteAsync(null).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task DownloadJavaAsync()
    {
        if (SelectedInstance?.Id is { } instanceId)
        {
            await Game.EnsureJavaForInstanceAsync(instanceId).ConfigureAwait(true);
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

    partial void OnSelectedInstanceChanged(ContentInstanceChoice? value)
    {
        if (_suppressInstanceFilter)
        {
            return;
        }

        OnPropertyChanged(nameof(HasTargetInstance));
        OnPropertyChanged(nameof(TargetInstanceHint));
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
