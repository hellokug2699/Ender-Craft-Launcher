// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.App.Services;
using Ecl.Core;
using Ecl.Core.Localization;
using Ecl.Download;
using Ecl.Minecraft.Content;
using Ecl.Minecraft.Instances;

namespace Ecl.App.ViewModels;

/// <summary>
/// “下载”页：实例下载（游戏/加载器）与 Mod、整合包、数据包、资源包、光影包下载都汇集在这里。
/// 没有实例时仍可搜索并下载文件；有实例时可以直接安装到实例。
/// </summary>
public sealed partial class DownloadViewModel : ObservableObject
{
    private readonly IContentSearchService _search;
    private readonly IContentInstallService _install;
    private readonly IInstanceService _instances;
    private readonly IDialogService _dialogs;
    private readonly ILocalizer _localizer;
    private readonly IDownloadController _downloads;
    private bool _initialized;
    private bool _suppressInstanceFilter;

    public InstancesViewModel Game { get; }

    public ObservableCollection<DownloadTabViewModel> Tabs { get; } = [];

    public ObservableCollection<ContentSourceChoice> Sources { get; } = [];

    public ObservableCollection<ContentInstanceChoice> TargetInstances { get; } = [];

    public ObservableCollection<LoaderFilterChoice> LoaderFilters { get; } = [];

    public ObservableCollection<ContentHitViewModel> Results { get; } = [];

    [ObservableProperty]
    private DownloadTabViewModel? _selectedTab;

    [ObservableProperty]
    private ContentSourceChoice? _selectedSource;

    [ObservableProperty]
    private ContentInstanceChoice? _selectedInstance;

    [ObservableProperty]
    private LoaderFilterChoice? _selectedLoaderFilter;

    [ObservableProperty]
    private ContentHitViewModel? _selectedResult;

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    private string _gameVersion = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private string _resultSummary = string.Empty;

    [ObservableProperty]
    private string _searchWarnings = string.Empty;

    [ObservableProperty]
    private string _reportText = string.Empty;

    [ObservableProperty]
    private bool _hasReport;

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _showProgress;

    [ObservableProperty]
    private bool _progressIndeterminate;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private string _progressText = string.Empty;

    [ObservableProperty]
    private string _downloadSpeedText = string.Empty;

    [ObservableProperty]
    private bool _canPauseDownload;

    [ObservableProperty]
    private bool _isDownloadPaused;

    [ObservableProperty]
    private string _downloadPauseButtonText = string.Empty;

    public bool IsGameTab => SelectedTab?.Kind is null;

    public bool IsContentTab => !IsGameTab;

    public bool IsModpackTab => SelectedTab?.Kind == ContentKind.ModPack;

    public bool HasSelectedResult => SelectedResult is not null;

    public bool CanInstallToInstance => SelectedResult is not null && SelectedInstance?.Id is not null;

    public bool HasTargetInstance => SelectedInstance?.Id is not null;

    public bool CanCreateInstanceFromPack => IsModpackTab && SelectedResult is not null;

    public string GameRoot { get; }

    public string TargetInstanceHint => SelectedInstance?.Id is null
        ? _localizer.Get("downloads.noInstanceHint")
        : _localizer.Get("downloads.instanceHint", SelectedInstance.Name);

    public DownloadViewModel(
        IContentSearchService search,
        IContentInstallService install,
        IInstanceService instances,
        InstancesViewModel game,
        IDialogService dialogs,
        ILocalizer localizer,
        IDownloadController downloads,
        IAppPaths paths)
    {
        _search = search;
        _install = install;
        _instances = instances;
        _dialogs = dialogs;
        _localizer = localizer;
        _downloads = downloads;
        Game = game;
        GameRoot = paths.GameRoot;

        Tabs.Add(new DownloadTabViewModel("game", null, localizer.Get("downloads.tab.game")));
        Tabs.Add(new DownloadTabViewModel("mod", ContentKind.Mod, localizer.Get("downloads.kind.mod")));
        Tabs.Add(new DownloadTabViewModel("modpack", ContentKind.ModPack, localizer.Get("downloads.kind.modpack")));
        Tabs.Add(new DownloadTabViewModel("datapack", ContentKind.DataPack, localizer.Get("downloads.kind.datapack")));
        Tabs.Add(new DownloadTabViewModel("resourcepack", ContentKind.ResourcePack, localizer.Get("downloads.kind.resourcepack")));
        Tabs.Add(new DownloadTabViewModel("shader", ContentKind.ShaderPack, localizer.Get("downloads.kind.shaderpack")));
        _selectedTab = Tabs[0];

        Sources.Add(new ContentSourceChoice(ContentSourceFilter.All, localizer.Get("downloads.source.all")));
        Sources.Add(new ContentSourceChoice(ContentSourceFilter.CurseForge, localizer.Get("downloads.source.curseforge")));
        Sources.Add(new ContentSourceChoice(ContentSourceFilter.Modrinth, localizer.Get("downloads.source.modrinth")));
        _selectedSource = Sources[0];

        LoaderFilters.Add(new LoaderFilterChoice(null, localizer.Get("downloads.loader.any")));
        LoaderFilters.Add(new LoaderFilterChoice("vanilla", localizer.Get("instances.loader.vanilla")));
        LoaderFilters.Add(new LoaderFilterChoice("fabric", localizer.Get("instances.loader.fabric")));
        LoaderFilters.Add(new LoaderFilterChoice("forge", localizer.Get("instances.loader.forge")));
        LoaderFilters.Add(new LoaderFilterChoice("neoforge", localizer.Get("instances.loader.neoforge")));
        LoaderFilters.Add(new LoaderFilterChoice("quilt", "Quilt"));
        _selectedLoaderFilter = LoaderFilters[0];

        DownloadPauseButtonText = localizer.Get("instances.download.pause");
    }

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        await RefreshInstancesAsync().ConfigureAwait(true);
    }

    public async Task ActivateAsync()
    {
        if (_initialized)
        {
            await RefreshInstancesAsync().ConfigureAwait(true);
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
            OnPropertyChanged(nameof(TargetInstanceHint));
        }
        finally
        {
            _suppressInstanceFilter = false;
        }
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (SelectedTab?.Kind is not { } kind || IsSearching)
        {
            return;
        }

        IsSearching = true;
        StatusMessage = _localizer.Get("downloads.searching");
        SearchWarnings = string.Empty;
        try
        {
            var result = await _search
                .SearchAsync(new ContentSearchQuery
                {
                    Query = Query,
                    Kind = kind,
                    Source = SelectedSource?.Filter ?? ContentSourceFilter.All,
                    GameVersion = string.IsNullOrWhiteSpace(GameVersion) ? null : GameVersion.Trim(),
                    Loader = SelectedLoaderFilter?.Id,
                    PageSize = 40,
                })
                .ConfigureAwait(true);

            if (!result.IsSuccess)
            {
                Results.Clear();
                ResultSummary = string.Empty;
                StatusMessage = _localizer.Get("downloads.searchFailed", result.Error?.Message ?? string.Empty);
                return;
            }

            Results.Clear();
            foreach (var hit in result.Value!.Hits)
            {
                Results.Add(new ContentHitViewModel(hit, _localizer));
            }

            SelectedResult = Results.FirstOrDefault();
            StatusMessage = _localizer.Get(
                "downloads.searchDone",
                result.Value.Hits.Count,
                result.Value.SourceTotals.GetValueOrDefault(ContentSource.CurseForge),
                result.Value.SourceTotals.GetValueOrDefault(ContentSource.Modrinth));

            ResultSummary = string.IsNullOrWhiteSpace(Query)
                ? _localizer.Get("downloads.popular")
                : _localizer.Get("downloads.resultFor", Query.Trim());
            SearchWarnings = string.Join(" ", result.Value.Warnings);
        }
        catch (Exception exception)
        {
            StatusMessage = _localizer.Get("downloads.searchFailed", exception.Message);
        }
        finally
        {
            IsSearching = false;
        }
    }

    [RelayCommand]
    private async Task CreateGameInstanceAsync()
    {
        await Game.CreateInstanceCommand.ExecuteAsync(null).ConfigureAwait(true);
        await RefreshInstancesAsync().ConfigureAwait(true);
        StatusMessage = Game.StatusMessage;

        if (Game.SelectedInstance is { } created)
        {
            SelectedInstance = TargetInstances.FirstOrDefault(item => item.Id == created.Model.Id);
        }
    }

    [RelayCommand]
    private async Task InstallGameAsync()
    {
        if (SelectedInstance?.Id is not { } instanceId)
        {
            StatusMessage = _localizer.Get("downloads.selectInstanceFirst");
            return;
        }

        Game.SelectedInstance = Game.Instances.FirstOrDefault(item => item.Model.Id == instanceId)
                                ?? Game.SelectedInstance;
        StatusMessage = _localizer.Get("downloads.installingGame");
        await Game.InstallCommand.ExecuteAsync(null).ConfigureAwait(true);
        StatusMessage = Game.InstallStatus;
    }

    [RelayCommand]
    private async Task DownloadJavaAsync()
    {
        if (SelectedInstance?.Id is not { } instanceId)
        {
            StatusMessage = _localizer.Get("downloads.selectInstanceFirst");
            return;
        }

        Game.SelectedInstance = Game.Instances.FirstOrDefault(item => item.Model.Id == instanceId)
                                ?? Game.SelectedInstance;
        await Game.DownloadJavaCommand.ExecuteAsync(null).ConfigureAwait(true);
        StatusMessage = Game.JavaDownloadStatus;
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        if (SelectedResult is not { } item || IsBusy)
        {
            return;
        }

        if (SelectedInstance?.Id is not { } instanceId)
        {
            StatusMessage = _localizer.Get("downloads.selectInstanceFirst");
            return;
        }

        await RunInstallAsync(
                item,
                progress => _install.InstallAsync(instanceId, item.Hit, progress),
                _localizer.Get("downloads.installing", item.Name))
            .ConfigureAwait(true);

        if (item.Hit.Kind == ContentKind.ModPack)
        {
            await RefreshInstancesAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task DownloadAsync()
    {
        if (SelectedResult is not { } item || IsBusy)
        {
            return;
        }

        await RunInstallAsync(
                item,
                progress => _install.DownloadAsync(item.Hit, progress),
                _localizer.Get("downloads.downloading", item.Name))
            .ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task InstallAsNewInstanceAsync()
    {
        if (SelectedResult is not { } item || IsBusy)
        {
            return;
        }

        if (item.Hit.Kind != ContentKind.ModPack)
        {
            return;
        }

        await RunInstallAsync(
                item,
                progress => _install.CreateInstanceAndImportModpackAsync(item.Hit, progress),
                _localizer.Get("downloads.installingPack", item.Name))
            .ConfigureAwait(true);
        await RefreshInstancesAsync().ConfigureAwait(true);
    }

    private async Task RunInstallAsync<T>(
        ContentHitViewModel item,
        Func<IProgress<ContentProgress>?, Task<Result<T>>> action,
        string initialStatus)
    {
        IsBusy = true;
        ShowProgress = true;
        ProgressIndeterminate = true;
        ProgressValue = 0;
        ProgressText = initialStatus;
        StatusMessage = initialStatus;
        HasReport = false;
        ReportText = string.Empty;
        BeginDownloadUi();

        var progress = new Progress<ContentProgress>(update =>
        {
            ProgressIndeterminate = update.TotalFiles == 0;
            ProgressValue = update.TotalFiles == 0
                ? 0
                : update.CompletedFiles * 100.0 / update.TotalFiles;
            ProgressText = update.TotalFiles > 0
                ? $"{update.Message} ({update.CompletedFiles}/{update.TotalFiles})"
                : update.Message;
            if (update.BytesPerSecond > 0)
            {
                DownloadSpeedText = FormatSpeed(update.BytesPerSecond);
            }
        });

        try
        {
            var result = await action(progress).ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                StatusMessage = result.Error?.ToString() ?? _localizer.Get("downloads.failed");
                ProgressText = StatusMessage;
                ReportText = StatusMessage;
                HasReport = true;
                return;
            }

            ProgressIndeterminate = false;
            ProgressValue = 100;
            ReportText = DescribeReport(item, result.Value!);
            HasReport = true;
            StatusMessage = _localizer.Get("downloads.done");
            ProgressText = _localizer.Get("downloads.done");
        }
        catch (Exception exception)
        {
            StatusMessage = _localizer.Get("downloads.failedDetail", exception.Message);
            ProgressText = StatusMessage;
            ReportText = StatusMessage;
            HasReport = true;
        }
        finally
        {
            IsBusy = false;
            ShowProgress = false;
            EndDownloadUi();
        }
    }

    private string DescribeReport(ContentHitViewModel item, object report) => report switch
    {
        ModpackImportResult import => _localizer.Get(
            "downloads.modpackReport",
            import.Name,
            import.DownloadedFiles,
            import.SkippedFiles,
            import.OverrideFiles,
            import.ArchivePath)
            + (string.IsNullOrWhiteSpace(import.InstanceId)
                ? string.Empty
                : Environment.NewLine + _localizer.Get("downloads.instanceCreated", import.InstanceId)
                  + Environment.NewLine + _localizer.Get("downloads.instanceNeedsInstall"))
            + FormatWarnings(import.Warnings),
        ContentInstallReport install => _localizer.Get(
            "downloads.installReport",
            install.Name,
            install.Files.Count,
            install.Files.FirstOrDefault() ?? string.Empty)
            + FormatWarnings(install.Warnings),
        ContentDownloadReport download => _localizer.Get(
            "downloads.downloadReport",
            download.Name,
            download.Path,
            download.Size),
        _ => item.Name,
    };

    private string FormatWarnings(IReadOnlyList<string> warnings) => warnings.Count == 0
        ? string.Empty
        : Environment.NewLine + _localizer.Get("downloads.reportWarnings", string.Join("；", warnings.Take(5)));

    [RelayCommand]
    private void ToggleDownloadPause()
    {
        if (!CanPauseDownload)
        {
            return;
        }

        if (IsDownloadPaused)
        {
            _downloads.Resume();
            IsDownloadPaused = false;
            DownloadPauseButtonText = _localizer.Get("instances.download.pause");
        }
        else
        {
            _downloads.Pause();
            IsDownloadPaused = true;
            DownloadPauseButtonText = _localizer.Get("instances.download.resume");
            DownloadSpeedText = _localizer.Get("instances.download.paused");
        }
    }

    private void BeginDownloadUi()
    {
        _downloads.Resume();
        CanPauseDownload = true;
        IsDownloadPaused = false;
        DownloadSpeedText = string.Empty;
        DownloadPauseButtonText = _localizer.Get("instances.download.pause");
    }

    private void EndDownloadUi()
    {
        _downloads.Resume();
        CanPauseDownload = false;
        IsDownloadPaused = false;
        DownloadSpeedText = string.Empty;
        DownloadPauseButtonText = _localizer.Get("instances.download.pause");
    }

    private static string FormatSpeed(double bytesPerSecond) => bytesPerSecond switch
    {
        >= 1024 * 1024 => $"{bytesPerSecond / 1024 / 1024:F1} MB/s",
        >= 1024 => $"{bytesPerSecond / 1024:F0} KB/s",
        > 0 => $"{bytesPerSecond:F0} B/s",
        _ => string.Empty,
    };

    [RelayCommand]
    private void OpenPage()
    {
        if (SelectedResult?.WebsiteUrl is not { Length: > 0 } url)
        {
            StatusMessage = _localizer.Get("downloads.selectResult");
            return;
        }

        if (!_dialogs.OpenUrl(url))
        {
            StatusMessage = _localizer.Get("downloads.openFailed");
        }
    }

    partial void OnSelectedTabChanged(DownloadTabViewModel? value)
    {
        OnPropertyChanged(nameof(IsGameTab));
        OnPropertyChanged(nameof(IsContentTab));
        OnPropertyChanged(nameof(IsModpackTab));
        OnPropertyChanged(nameof(CanCreateInstanceFromPack));

        Results.Clear();
        SelectedResult = null;
        ResultSummary = string.Empty;
        SearchWarnings = string.Empty;
        StatusMessage = string.Empty;
        HasReport = false;
        ReportText = string.Empty;

        if (_initialized && value?.Kind is not null)
        {
            _ = SearchAsync();
        }
    }

    partial void OnSelectedSourceChanged(ContentSourceChoice? value)
    {
        if (_initialized && IsContentTab)
        {
            _ = SearchAsync();
        }
    }

    partial void OnSelectedInstanceChanged(ContentInstanceChoice? value)
    {
        OnPropertyChanged(nameof(CanInstallToInstance));
        OnPropertyChanged(nameof(HasTargetInstance));
        OnPropertyChanged(nameof(TargetInstanceHint));

        if (_suppressInstanceFilter)
        {
            return;
        }

        if (value?.Id is not null)
        {
            GameVersion = value.GameVersion;
            SelectedLoaderFilter = LoaderFilters.FirstOrDefault(item =>
                string.Equals(item.Id, value.Loader, StringComparison.OrdinalIgnoreCase)) ?? LoaderFilters[0];
        }
        else
        {
            GameVersion = string.Empty;
            SelectedLoaderFilter = LoaderFilters[0];
        }
    }

    partial void OnSelectedResultChanged(ContentHitViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelectedResult));
        OnPropertyChanged(nameof(CanInstallToInstance));
        OnPropertyChanged(nameof(CanCreateInstanceFromPack));
    }
}

public sealed record DownloadTabViewModel(string Key, ContentKind? Kind, string Title);

public sealed record ContentSourceChoice(ContentSourceFilter Filter, string Title);

public sealed record ContentInstanceChoice(string? Id, string Name, string GameVersion, string Loader);

public sealed record LoaderFilterChoice(string? Id, string Title);

public sealed class ContentHitViewModel
{
    public ContentHitViewModel(ContentHit hit, ILocalizer localizer)
    {
        Hit = hit;
        Name = hit.Name;
        Summary = string.IsNullOrWhiteSpace(hit.Summary) ? "—" : hit.Summary;
        AuthorText = string.IsNullOrWhiteSpace(hit.Author)
            ? hit.SourceLabel
            : $"{hit.Author} · {hit.SourceLabel}";
        SourceLabel = hit.SourceLabel;
        IsModrinth = hit.Source == ContentSource.Modrinth;
        Meta = localizer.Get("downloads.downloadsCount", hit.DownloadCount) +
               (hit.UpdatedAt is { } updated ? $" · {updated.ToLocalTime():yyyy-MM-dd}" : string.Empty);
        WebsiteUrl = hit.WebsiteUrl;
        IconUrl = hit.IconUrl;
    }

    public ContentHit Hit { get; }

    public string Name { get; }

    public string Summary { get; }

    public string AuthorText { get; }

    public string SourceLabel { get; }

    public bool IsModrinth { get; }

    public string Meta { get; }

    public string? WebsiteUrl { get; }

    public string? IconUrl { get; }
}
