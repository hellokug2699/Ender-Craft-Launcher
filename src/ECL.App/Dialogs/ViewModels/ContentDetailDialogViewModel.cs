// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.App.Dialogs;
using Ecl.App.ViewModels;
using Ecl.Core;
using Ecl.Core.Localization;
using Ecl.Minecraft.Content;

namespace Ecl.App.Dialogs.ViewModels;

/// <summary>
/// 下载中心内容详情弹窗（`download-mods.md` §6 / `download-modpacks.md` §7 /
/// `download-resource-template.md` §7）：来源信息 + 版本列表。
/// 有筛选条件时上方是“筛选的版本”，下方是“全部版本”，允许重复；两表都属于同一个弹窗。
/// 详情只呈现被点击来源的数据，不跨来源拼接。
/// </summary>
public sealed partial class ContentDetailDialogViewModel : ObservableObject, IDialogSessionAware
{
    private readonly Func<CancellationToken, Task<Result<ContentVersionCatalogResult>>> _load;
    private readonly Func<ContentVersion, ContentVersionRowViewModel> _rowFactory;
    private readonly Func<ContentVersionRowViewModel, Task> _download;
    private readonly Func<string, bool> _openUrl;
    private readonly CancellationTokenSource _cancellation = new();
    private IDialogSession? _session;
    private bool _loadStarted;

    public ContentDetailDialogViewModel(
        ContentHit hit,
        ContentKind kind,
        string kindLabel,
        string filterTitle,
        bool hasFilter,
        Func<ContentVersion, bool>? matcher,
        ILocalizer localizer,
        Func<CancellationToken, Task<Result<ContentVersionCatalogResult>>> load,
        Func<ContentVersion, ContentVersionRowViewModel> rowFactory,
        Func<ContentVersionRowViewModel, Task> download,
        Func<string, bool> openUrl)
    {
        Hit = hit;
        Kind = kind;
        KindLabel = kindLabel;
        FilterTitle = filterTitle;
        HasFilter = hasFilter;
        Matcher = matcher;
        _load = load;
        _rowFactory = rowFactory;
        _download = download;
        _openUrl = openUrl;

        Localizer = localizer;
        Name = hit.Name;
        Summary = string.IsNullOrWhiteSpace(hit.Summary)
            ? localizer.Get("downloads.discovery.noSummary")
            : hit.Summary;
        AuthorText = string.IsNullOrWhiteSpace(hit.Author)
            ? localizer.Get("downloads.discovery.authorUnknown")
            : localizer.Get("downloads.discovery.author", hit.Author);
        SourceLabel = hit.SourceLabel;
        MetaText = localizer.Get("downloads.downloadsCount", hit.DownloadCount) +
                   (hit.UpdatedAt is { } updated
                       ? $" · {ContentDiscoveryPresentation.FormatDate(updated, localizer)}"
                       : string.Empty);
        WebsiteUrl = hit.WebsiteUrl;
        AllVersionsTitle = localizer.Get("downloads.discovery.versionsAll");
        VersionColumnText = localizer.Get("downloads.discovery.columns.version");
        GameVersionColumnText = localizer.Get("downloads.discovery.columns.gameVersion");
        LoaderColumnText = localizer.Get("downloads.discovery.columns.loader");
        SizeColumnText = localizer.Get("downloads.discovery.columns.size");
        DateColumnText = localizer.Get("downloads.discovery.columns.date");
        DownloadText = localizer.Get("downloads.discovery.download");
        CloseText = localizer.Get("dialog.close");
        OpenWebsiteText = localizer.Get("downloads.discovery.openSource");
        FilteredEmptyText = localizer.Get("downloads.discovery.filteredEmpty");
        AllEmptyText = localizer.Get("downloads.discovery.versionsEmpty");
        LoadingText = localizer.Get("downloads.discovery.versionsLoading");
        ShowModpackNote = kind == ContentKind.ModPack;
        ModpackNote = localizer.Get("downloads.discovery.modpackBlocked");
    }

    public ILocalizer Localizer { get; }

    public ContentHit Hit { get; }

    public ContentKind Kind { get; }

    public string KindLabel { get; }

    public string Name { get; }

    public string Summary { get; }

    public string AuthorText { get; }

    public string SourceLabel { get; }

    public string MetaText { get; }

    public string? WebsiteUrl { get; }

    public bool CanOpenWebsite => !string.IsNullOrWhiteSpace(WebsiteUrl);

    public string AllVersionsTitle { get; }

    public string FilterTitle { get; }

    public bool HasFilter { get; }

    public Func<ContentVersion, bool>? Matcher { get; }

    public string VersionColumnText { get; }

    public string GameVersionColumnText { get; }

    public string LoaderColumnText { get; }

    public string SizeColumnText { get; }

    public string DateColumnText { get; }

    public string DownloadText { get; }

    public string CloseText { get; }

    public string OpenWebsiteText { get; }

    public string FilteredEmptyText { get; }

    public string AllEmptyText { get; }

    public string LoadingText { get; }

    public bool ShowModpackNote { get; }

    public string ModpackNote { get; }

    public ObservableCollection<ContentVersionRowViewModel> FilteredVersions { get; } = [];

    public ObservableCollection<ContentVersionRowViewModel> AllVersions { get; } = [];

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _loadError = string.Empty;

    [ObservableProperty]
    private bool _isComplete = true;

    [ObservableProperty]
    private string _incompleteNote = string.Empty;

    public bool HasLoadError => !string.IsNullOrWhiteSpace(LoadError);

    public bool FilteredEmpty => !IsLoading && !HasLoadError && FilteredVersions.Count == 0;

    public bool AllEmpty => !IsLoading && !HasLoadError && AllVersions.Count == 0;

    public string FilterCountText => Localizer.Get("downloads.discovery.versionsCount", FilteredVersions.Count);

    public string AllCountText => Localizer.Get("downloads.discovery.versionsCount", AllVersions.Count);

    void IDialogSessionAware.Attach(IDialogSession session) => _session = session;

    /// <summary>打开后由视图触发一次；重复调用不会重复请求。</summary>
    public async Task LoadAsync()
    {
        if (_loadStarted)
        {
            return;
        }

        _loadStarted = true;
        IsLoading = true;
        LoadError = string.Empty;
        try
        {
            var result = await _load(_cancellation.Token).ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                LoadError = Localizer.Get(
                    "downloads.discovery.versionsFailed",
                    result.Error?.Message ?? string.Empty);
                return;
            }

            foreach (var version in result.Value!.Versions)
            {
                var row = _rowFactory(version);
                AllVersions.Add(row);
                if (Matcher is null || Matcher(version))
                {
                    FilteredVersions.Add(row);
                }
            }

            IsComplete = result.Value.IsComplete;
            IncompleteNote = IsComplete
                ? string.Empty
                : Localizer.Get("downloads.discovery.versionsIncomplete");
        }
        catch (OperationCanceledException)
        {
            // 关闭弹窗取消读取是预期行为。
        }
        catch (Exception exception)
        {
            LoadError = Localizer.Get("downloads.discovery.versionsFailed", exception.Message);
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(HasLoadError));
            OnPropertyChanged(nameof(FilteredEmpty));
            OnPropertyChanged(nameof(AllEmpty));
            OnPropertyChanged(nameof(FilterCountText));
            OnPropertyChanged(nameof(AllCountText));
        }
    }

    [RelayCommand]
    private async Task DownloadVersionAsync(ContentVersionRowViewModel? row)
    {
        if (row is null || !row.CanRequestDownload)
        {
            return;
        }

        await _download(row).ConfigureAwait(true);
    }

    [RelayCommand]
    private void OpenWebsite()
    {
        if (WebsiteUrl is { Length: > 0 } url)
        {
            _openUrl(url);
        }
    }

    [RelayCommand]
    private void Close()
    {
        _cancellation.Cancel();
        _session?.TryClose(DialogOutcome.Cancel);
    }

    partial void OnLoadErrorChanged(string value) => OnPropertyChanged(nameof(HasLoadError));

    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(FilteredEmpty));
        OnPropertyChanged(nameof(AllEmpty));
    }
}
