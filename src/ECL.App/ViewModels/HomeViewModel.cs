// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.Core;
using Ecl.Core.Localization;
using Ecl.Minecraft.News;

namespace Ecl.App.ViewModels;

/// <summary>
/// 主页只负责两件事：启动游戏，以及承载以后的新版本情报。
/// 具体下载与实例管理分别由 <see cref="DownloadViewModel"/> 与 <see cref="InstancesViewModel"/> 负责。
/// </summary>
public sealed partial class HomeViewModel : ObservableObject
{
    private readonly IVersionNewsService _news;
    private readonly ILocalizer _localizer;
    private bool _newsLoaded;

    public InstancesViewModel Launcher { get; }

    public ObservableCollection<VersionNewsItemViewModel> News { get; } = [];

    [ObservableProperty]
    private string _newsStatus = string.Empty;

    [ObservableProperty]
    private bool _isLoadingNews;

    public string GameRoot { get; }

    public HomeViewModel(
        InstancesViewModel launcher,
        IVersionNewsService news,
        ILocalizer localizer,
        IAppPaths paths)
    {
        Launcher = launcher;
        _news = news;
        _localizer = localizer;
        GameRoot = paths.GameRoot;
        NewsStatus = localizer.Get("home.news.loading");
    }

    public async Task InitializeAsync()
    {
        if (_newsLoaded)
        {
            return;
        }

        _newsLoaded = true;
        await RefreshNewsAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RefreshNewsAsync()
    {
        if (IsLoadingNews)
        {
            return;
        }

        IsLoadingNews = true;
        NewsStatus = _localizer.Get("home.news.loading");
        try
        {
            var result = await _news.GetLatestAsync().ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                NewsStatus = _localizer.Get("home.news.unavailable");
                return;
            }

            News.Clear();
            foreach (var item in result.Value!)
            {
                News.Add(new VersionNewsItemViewModel(item, _localizer));
            }

            NewsStatus = News.Count == 0
                ? _localizer.Get("home.news.empty")
                : _localizer.Get("home.news.source");
        }
        catch (Exception exception)
        {
            NewsStatus = exception.Message;
        }
        finally
        {
            IsLoadingNews = false;
        }
    }
}

public sealed class VersionNewsItemViewModel
{
    public VersionNewsItemViewModel(VersionNewsItem item, ILocalizer localizer)
    {
        Version = item.Version;
        Type = item.Type;
        TypeLabel = localizer.Get($"home.news.type.{item.Type.ToLowerInvariant()}");
        DateText = item.ReleasedAt is null
            ? string.Empty
            : item.ReleasedAt.Value.ToLocalTime().ToString("yyyy-MM-dd");
        Url = item.Url;
    }

    public string Version { get; }

    public string Type { get; }

    public string TypeLabel { get; }

    public string DateText { get; }

    public string Url { get; }
}
