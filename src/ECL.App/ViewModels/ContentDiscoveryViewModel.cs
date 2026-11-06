// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.App.Dialogs;
using Ecl.App.Dialogs.ViewModels;
using Ecl.App.Services;
using Ecl.Configuration.Settings;
using Ecl.Core;
using Ecl.Core.Localization;
using Ecl.Minecraft.Content;
using Ecl.Minecraft.Instances;
using Ecl.Minecraft.Mods;
using Ecl.Minecraft.Versions;

namespace Ecl.App.ViewModels;

/// <summary>
/// 下载中心内容发现页（`download-mods.md` / `download-modpacks.md` / `download-resource-template.md`）。
/// 模组、整合包、资源包、光影包与数据包共用同一套页面对象与视图，只替换分类名称、
/// 目标语义与适用性字段。
/// <para>
/// 边界：
/// - 搜索只接受英文；中文输入立即标红且不提交。
/// - 目标实例、版本与加载器筛选来自真实数据；已有实例模式下版本与加载器锁定。
/// - 版本行由来源 API 重新解析后安装；页内只有条级进度，全局任务状态归 Output/Task。
/// - 整合包部署需要实例版本页的整合包模式（B4b），数据包需要世界目标；能力未接入前置灰真实原因。
/// </para>
/// </summary>
public sealed partial class ContentDiscoveryViewModel : ObservableObject
{
    private readonly IContentSearchService _search;
    private readonly IContentVersionCatalog _catalog;
    private readonly IContentInstallService _install;
    private readonly IInstanceService _instances;
    private readonly IVersionCatalog _versionCatalog;
    private readonly IDialogEngine _dialogEngine;
    private readonly IDialogService _dialogs;
    private readonly ILocalizer _localizer;
    private readonly IShaderSupportService _shaderSupport;
    private readonly SemaphoreSlim _downloadGate;
    private readonly Dictionary<string, ContentVersionRowViewModel> _versionRows = new(StringComparer.Ordinal);
    private readonly List<Task> _downloadTasks = [];
    private Task? _initializeTask;
    private bool _suppressFilterReset;
    private bool _hasSearched;

    public ContentDiscoveryViewModel(
        ContentKind kind,
        IContentSearchService search,
        IContentVersionCatalog catalog,
        IContentInstallService install,
        IInstanceService instances,
        IVersionCatalog versionCatalog,
        ISettingsService settings,
        IDialogEngine dialogEngine,
        IDialogService dialogs,
        ILocalizer localizer,
        IShaderSupportService shaderSupport)
    {
        Kind = kind;
        _search = search;
        _catalog = catalog;
        _install = install;
        _instances = instances;
        _versionCatalog = versionCatalog;
        _dialogEngine = dialogEngine;
        _dialogs = dialogs;
        _localizer = localizer;
        _shaderSupport = shaderSupport;

        var concurrency = Math.Clamp(settings.Get<int>("network.maxConcurrentDownloads"), 1, 64);
        _downloadGate = new SemaphoreSlim(concurrency, concurrency);

        Sources.Add(new ContentSourceChoice(ContentSourceFilter.All, localizer.Get("downloads.discovery.source.all")));
        Sources.Add(new ContentSourceChoice(ContentSourceFilter.CurseForge, localizer.Get("downloads.discovery.source.curseforge")));
        Sources.Add(new ContentSourceChoice(ContentSourceFilter.Modrinth, localizer.Get("downloads.discovery.source.modrinth")));
        _selectedSource = Sources[0];

        LoaderChoices.Add(new LoaderFilterChoice(null, localizer.Get("downloads.discovery.target.any")));
        LoaderChoices.Add(new LoaderFilterChoice("forge", localizer.Get("downloads.discovery.loader.forge")));
        LoaderChoices.Add(new LoaderFilterChoice("fabric", localizer.Get("downloads.discovery.loader.fabric")));
        LoaderChoices.Add(new LoaderFilterChoice("neoforge", localizer.Get("downloads.discovery.loader.neoforge")));
        _selectedLoader = LoaderChoices[0];
    }

    public ContentKind Kind { get; }

    public string Key => Kind switch
    {
        ContentKind.Mod => "mod",
        ContentKind.ModPack => "modpack",
        ContentKind.ResourcePack => "resourcepack",
        ContentKind.ShaderPack => "shaderpack",
        ContentKind.DataPack => "datapack",
        _ => "mod",
    };

    public string Title => _localizer.Get($"downloads.discovery.title.{Key}");

    public string Subtitle => _localizer.Get($"downloads.discovery.subtitle.{Key}");

    public string KindLabel => _localizer.Get(ContentKinds.DisplayKey(Kind));

    public string SearchPlaceholder => _localizer.Get("downloads.searchPlaceholder");

    public string SearchText => _localizer.Get("downloads.search");

    public string EnglishOnlyHint => _localizer.Get("downloads.discovery.englishOnly");

    public string SourceLabel => _localizer.Get("downloads.discovery.source.label");

    public string TargetInstanceLabel => _localizer.Get("downloads.discovery.target.instance");

    public string GameVersionLabel => _localizer.Get("instances.version");

    public string LoaderLabel => _localizer.Get("instances.loader.title");

    public string ExistingInstanceModeText => _localizer.Get("downloads.discovery.target.existingMode");

    public string PickFolderTitle => _localizer.Get("downloads.discovery.pickFolderTitle");

    /// <summary>模组页提供“为已有实例下载模组”复选框；资源包/光影包固定要求已登记实例。</summary>
    public bool ShowsTargetModeCheckBox => Kind == ContentKind.Mod;

    /// <summary>整合包只能创建新实例，不提供已有实例目标模式（`download-modpacks.md` §5）。</summary>
    public bool ShowsCustomFilters =>
        Kind == ContentKind.ModPack || (Kind == ContentKind.Mod && !UseExistingInstance);

    public bool ShowsInstanceSelector =>
        Kind is ContentKind.ResourcePack or ContentKind.ShaderPack ||
        (Kind == ContentKind.Mod && UseExistingInstance);

    public bool IsTargetLocked =>
        Kind is ContentKind.ResourcePack or ContentKind.ShaderPack ||
        (Kind == ContentKind.Mod && UseExistingInstance);

    public bool IsUnavailable => Kind == ContentKind.DataPack;

    public string UnavailableReason => _localizer.Get("downloads.discovery.datapackBlocked");

    public bool ShowsShaderSupport => Kind == ContentKind.ShaderPack;

    /// <summary>整合包部署模式属于 B4b：本页只提供发现与版本查看。</summary>
    public bool CanRequestDownloads => Kind != ContentKind.ModPack && !IsUnavailable;

    public ObservableCollection<ContentSourceChoice> Sources { get; } = [];

    public ObservableCollection<LoaderFilterChoice> LoaderChoices { get; } = [];

    public ObservableCollection<VersionChoiceViewModel> GameVersions { get; } = [];

    public ObservableCollection<ContentInstanceChoice> TargetInstances { get; } = [];

    public ObservableCollection<ContentResultRowViewModel> Results { get; } = [];

    [ObservableProperty]
    private ContentSourceChoice? _selectedSource;

    [ObservableProperty]
    private LoaderFilterChoice? _selectedLoader;

    [ObservableProperty]
    private VersionChoiceViewModel? _selectedGameVersion;

    [ObservableProperty]
    private ContentInstanceChoice? _selectedInstance;

    [ObservableProperty]
    private bool _useExistingInstance;

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private string _resultSummary = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private string _searchWarnings = string.Empty;

    [ObservableProperty]
    private string _shaderSupportNote = string.Empty;

    [ObservableProperty]
    private bool _isInitialized;

    public bool IsSearchInputInvalid => ContentDiscoveryPresentation.HasNonAsciiInput(Query);

    public bool CanSearch => !IsSearching && !IsUnavailable;

    public bool HasResults => Results.Count > 0;

    public bool HasSearched => _hasSearched;

    public bool IsEmptyResult => _hasSearched && Results.Count == 0;

    public bool HasInstances => TargetInstances.Count > 0;

    public bool ShowNoInstances => IsTargetLocked && !HasInstances;

    public bool HasTargetInstance => SelectedInstance?.Id is not null;

    public string InstanceHint => IsTargetLocked
        ? HasTargetInstance
            ? _localizer.Get(Kind is ContentKind.ResourcePack or ContentKind.ShaderPack
                ? "downloads.discovery.target.lockedContent"
                : "downloads.discovery.target.locked")
            : _localizer.Get("downloads.discovery.target.none")
        : Kind == ContentKind.ModPack
            ? _localizer.Get("downloads.discovery.target.modpack")
            : _localizer.Get("downloads.discovery.target.custom");

    public string EmptyResultText => _localizer.Get("downloads.discovery.empty");

    public string ListHint => _localizer.Get("downloads.discovery.listHint");

    public string NoInstancesText => _localizer.Get("downloads.discovery.noInstances");

    public string ModpackCapabilityNote => _localizer.Get("downloads.discovery.modpackBlocked");

    /// <summary>等待初始化（实例列表 + 版本清单）完成；重复调用复用同一任务。</summary>
    public Task EnsureLoadedAsync() => _initializeTask ??= InitializeAsync();

    /// <summary>进入页面（首次激活）时刷新实例并保留已配置的筛选。</summary>
    public async Task ActivateAsync()
    {
        await EnsureLoadedAsync().ConfigureAwait(true);
        await RefreshInstancesAsync().ConfigureAwait(true);
        await RefreshShaderSupportAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// 从实例工作区模组/资源包/光影包页带入稳定 InstanceId 与搜索词
    /// （`download-mods.md` §4、`download-resource-template.md` §5）。
    /// 目标实例已失效时明确要求重新选择，不静默退化为自定义筛选或任意目录。
    /// </summary>
    public async Task ActivateForInstanceAsync(string instanceId, string? query)
    {
        await EnsureLoadedAsync().ConfigureAwait(true);
        await RefreshInstancesAsync().ConfigureAwait(true);

        var target = TargetInstances.FirstOrDefault(item =>
            string.Equals(item.Id, instanceId, StringComparison.Ordinal));
        if (target is null)
        {
            _suppressFilterReset = true;
            try
            {
                SelectedInstance = TargetInstances.FirstOrDefault();
            }
            finally
            {
                _suppressFilterReset = false;
            }

            ClearResults();
            StatusMessage = _localizer.Get("downloads.instanceMissing");
            return;
        }

        _suppressFilterReset = true;
        try
        {
            UseExistingInstance = Kind == ContentKind.Mod;
            SelectedInstance = target;
        }
        finally
        {
            _suppressFilterReset = false;
        }

        Query = query ?? string.Empty;
        await RefreshShaderSupportAsync().ConfigureAwait(true);
        await SearchAsync().ConfigureAwait(true);
    }

    private async Task InitializeAsync()
    {
        await RefreshInstancesAsync().ConfigureAwait(true);
        await RefreshShaderSupportAsync().ConfigureAwait(true);
        await LoadVersionChoicesAsync().ConfigureAwait(true);
        IsInitialized = true;
        OnPropertyChanged(nameof(IsSearchInputInvalid));
    }

    private async Task LoadVersionChoicesAsync()
    {
        var result = await _versionCatalog.GetVersionsAsync().ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            StatusMessage = _localizer.Get("downloads.discovery.versionsLoadFailed", result.Error?.Message ?? string.Empty);
            return;
        }

        GameVersions.Clear();
        var releases = result.Value!
            .Where(item => item.Kind == MinecraftVersionKind.Release)
            .Select(item => item.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var version in result.Value!
                     .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                     .Select(group => group.First())
                     .OrderByDescending(item => item.ReleasedAt ?? DateTimeOffset.MinValue))
        {
            GameVersions.Add(new VersionChoiceViewModel(version.Id, version.Id));
        }

        _suppressFilterReset = true;
        try
        {
            // 默认选择最新的正式版；快照/愚人节版仍在列表中，只是不抢占默认值。
            SelectedGameVersion = GameVersions.FirstOrDefault(choice => choice.Value is { } id && releases.Contains(id))
                                  ?? GameVersions.FirstOrDefault();
        }
        finally
        {
            _suppressFilterReset = false;
        }
    }

    private async Task RefreshInstancesAsync()
    {
        var selectedId = SelectedInstance?.Id;
        var instances = await _instances.GetInstancesAsync().ConfigureAwait(true);

        _suppressFilterReset = true;
        try
        {
            TargetInstances.Clear();
            foreach (var instance in instances)
            {
                TargetInstances.Add(new ContentInstanceChoice(
                    instance.Id,
                    instance.Name,
                    instance.GameVersion,
                    instance.Loader));
            }

            SelectedInstance = TargetInstances.FirstOrDefault(item => item.Id == selectedId)
                               ?? (IsTargetLocked ? TargetInstances.FirstOrDefault() : null);
        }
        finally
        {
            _suppressFilterReset = false;
        }

        OnPropertyChanged(nameof(HasInstances));
        OnPropertyChanged(nameof(ShowNoInstances));
        OnPropertyChanged(nameof(HasTargetInstance));
        OnPropertyChanged(nameof(InstanceHint));
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (IsSearching || IsUnavailable)
        {
            return;
        }

        if (IsSearchInputInvalid)
        {
            StatusMessage = EnglishOnlyHint;
            return;
        }

        if (IsTargetLocked && !HasTargetInstance)
        {
            ClearResults();
            StatusMessage = _localizer.Get("downloads.discovery.target.none");
            return;
        }

        var gameVersion = FilterGameVersion;
        var loader = FilterLoader;
        IsSearching = true;
        StatusMessage = _localizer.Get("downloads.searching");
        SearchWarnings = string.Empty;
        try
        {
            var result = await _search
                .SearchAsync(new ContentSearchQuery
                {
                    Query = string.IsNullOrWhiteSpace(Query) ? null : Query.Trim(),
                    Kind = Kind,
                    Source = SelectedSource?.Filter ?? ContentSourceFilter.All,
                    GameVersion = string.IsNullOrWhiteSpace(gameVersion) ? null : gameVersion,
                    Loader = ContentKinds.UsesLoader(Kind) ? loader : null,
                    PageSize = 40,
                })
                .ConfigureAwait(true);

            if (!result.IsSuccess)
            {
                ClearResults();
                _hasSearched = false;
                StatusMessage = _localizer.Get("downloads.searchFailed", result.Error?.Message ?? string.Empty);
                return;
            }

            Results.Clear();
            foreach (var hit in result.Value!.Hits)
            {
                Results.Add(new ContentResultRowViewModel(hit, Kind, _localizer));
            }

            _hasSearched = true;
            OnPropertyChanged(nameof(HasResults));
            OnPropertyChanged(nameof(HasSearched));
            OnPropertyChanged(nameof(IsEmptyResult));

            ResultSummary = string.IsNullOrWhiteSpace(Query)
                ? _localizer.Get("downloads.popular")
                : _localizer.Get("downloads.resultFor", Query.Trim());
            StatusMessage = result.Value.Hits.Count == 0
                ? EmptyResultText
                : _localizer.Get(
                    "downloads.searchDone",
                    result.Value.Hits.Count,
                    result.Value.SourceTotals.GetValueOrDefault(ContentSource.CurseForge),
                    result.Value.SourceTotals.GetValueOrDefault(ContentSource.Modrinth));
            SearchWarnings = string.Join(" ", result.Value.Warnings);
        }
        catch (Exception exception)
        {
            ClearResults();
            _hasSearched = false;
            StatusMessage = _localizer.Get("downloads.searchFailed", exception.Message);
        }
        finally
        {
            IsSearching = false;
        }
    }

    [RelayCommand]
    private async Task OpenDetailAsync(ContentResultRowViewModel? row)
    {
        if (row is null || IsUnavailable)
        {
            return;
        }

        if (!_dialogEngine.IsAvailable)
        {
            StatusMessage = _localizer.Get("downloads.discovery.dialogUnavailable");
            return;
        }

        var gameVersion = FilterGameVersion;
        var loader = FilterLoader;
        var hasFilter = !string.IsNullOrWhiteSpace(gameVersion);
        Func<ContentVersion, bool>? matcher = null;
        if (hasFilter)
        {
            matcher = string.IsNullOrWhiteSpace(loader)
                ? version => version.MatchesGameVersion(gameVersion)
                : version => version.MatchesGameVersion(gameVersion) && version.MatchesLoader(loader);
        }

        var dialog = new ContentDetailDialogViewModel(
            row.Hit,
            Kind,
            KindLabel,
            hasFilter ? ContentDiscoveryPresentation.FormatFilterTitle(gameVersion!, loader, _localizer) : string.Empty,
            hasFilter,
            matcher,
            _localizer,
            async cancellationToken => await _catalog.GetVersionsAsync(row.Hit, cancellationToken)
                .ConfigureAwait(true),
            version => GetOrCreateVersionRow(row.Hit, version),
            StartDownloadFlowAsync,
            url => _dialogs.OpenUrl(url));

        await _dialogEngine.ShowAsync(
                new DialogRequest
                {
                    Title = _localizer.Get("downloads.discovery.detailTitle"),
                    Content = dialog,
                    MinWidth = 640,
                    MaxWidth = 880,
                    MaxHeight = 700,
                    InitialFocus = DialogInitialFocus.FirstFocusable,
                })
            .ConfigureAwait(true);
    }

    private string? FilterGameVersion => IsTargetLocked
        ? SelectedInstance?.GameVersion
        : SelectedGameVersion?.Value;

    private string? FilterLoader
    {
        get
        {
            if (Kind is not (ContentKind.Mod or ContentKind.ModPack))
            {
                return null;
            }

            if (!IsTargetLocked)
            {
                return SelectedLoader?.Id;
            }

            return SelectedInstance?.Loader?.Trim().ToLowerInvariant() switch
            {
                "fabric" => "fabric",
                "forge" or "cleanroom" => "forge",
                "neoforge" => "neoforge",
                "quilt" => "quilt",
                _ => null,
            };
        }
    }

    private ContentInstanceChoice? Target => IsTargetLocked ? SelectedInstance : null;

    private ContentVersionRowViewModel GetOrCreateVersionRow(ContentHit hit, ContentVersion version)
    {
        var key = $"{hit.Key}|{version.VersionId}";
        if (_versionRows.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var row = new ContentVersionRowViewModel(
            hit,
            version,
            CanRequestDownloads,
            CanRequestDownloads ? null : ModpackCapabilityNote,
            _localizer);
        _versionRows[key] = row;
        return row;
    }

    /// <summary>
    /// 点击版本行下载按钮后的目标确认流程：已有实例 → 确认弹窗；自定义筛选 → 先选目录再确认。
    /// 「安装到其他位置」总是重新选择合法目标，不静默改变本次安装目标（§7）。
    /// </summary>
    private async Task StartDownloadFlowAsync(ContentVersionRowViewModel row)
    {
        if (row.IsActive)
        {
            return;
        }

        if (!row.CanRequestDownload)
        {
            StatusMessage = row.DownloadHint;
            return;
        }

        if (!_dialogEngine.IsAvailable)
        {
            StatusMessage = _localizer.Get("downloads.discovery.dialogUnavailable");
            return;
        }

        if (Target is { Id: { } instanceId } instance)
        {
            var confirm = await ShowConfirmAsync(
                    _localizer.Get("downloads.discovery.confirmInstance", instance.Name))
                .ConfigureAwait(true);
            if (confirm.IsPrimary)
            {
                StartInstall(row, instanceId, instance.Name);
            }
            else if (confirm.Outcome == DialogOutcome.Secondary)
            {
                await ChooseOtherTargetAsync(row).ConfigureAwait(true);
            }

            return;
        }

        if (IsTargetLocked)
        {
            StatusMessage = _localizer.Get("downloads.discovery.target.none");
            return;
        }

        await PickDirectoryAndStartAsync(row).ConfigureAwait(true);
    }

    private async Task PickDirectoryAndStartAsync(ContentVersionRowViewModel row)
    {
        var directory = _dialogs.PickFolder(PickFolderTitle);
        while (!string.IsNullOrWhiteSpace(directory))
        {
            var confirm = await ShowConfirmAsync(
                    _localizer.Get("downloads.discovery.confirmLocation", directory))
                .ConfigureAwait(true);
            if (confirm.IsPrimary)
            {
                StartDirectoryDownload(row, directory);
                return;
            }

            if (confirm.Outcome != DialogOutcome.Secondary)
            {
                return;
            }

            directory = _dialogs.PickFolder(PickFolderTitle);
        }
    }

    private async Task ChooseOtherTargetAsync(ContentVersionRowViewModel row)
    {
        var dialog = new ContentTargetDialogViewModel(
            TargetInstances
                .Where(item => item.Id is not null)
                .Select(item => new ContentTargetInstanceRow(
                    item.Id!,
                    item.Name,
                    ContentDiscoveryPresentation.FormatInstanceMeta(item.GameVersion, item.Loader, _localizer)))
                .ToArray(),
            Kind == ContentKind.Mod,
            _dialogs,
            _localizer,
            PickFolderTitle);

        var result = await _dialogEngine.ShowAsync(
                new DialogRequest
                {
                    Title = _localizer.Get("downloads.discovery.otherTitle"),
                    Content = dialog,
                    MinWidth = 440,
                    MaxWidth = 600,
                    MaxHeight = 540,
                })
            .ConfigureAwait(true);

        if (result.Outcome != DialogOutcome.Primary || result.Value is not ContentTargetChoice choice)
        {
            return;
        }

        if (choice.InstanceId is { } instanceId)
        {
            StartInstall(row, instanceId, choice.InstanceName ?? instanceId);
        }
        else if (choice.Directory is { Length: > 0 } directory)
        {
            StartDirectoryDownload(row, directory);
        }
    }

    private Task<DialogResult> ShowConfirmAsync(string message) =>
        _dialogEngine.ShowAsync(new DialogRequest
        {
            Title = _localizer.Get("downloads.discovery.confirmTitle"),
            Content = new MessageDialogViewModel(
                message,
                _localizer.Get("downloads.discovery.install"),
                _localizer.Get("downloads.discovery.installOther"),
                _localizer.Get("dialog.cancel")),
            InitialFocus = DialogInitialFocus.Primary,
            MinWidth = 380,
            MaxWidth = 500,
        });

    private void StartInstall(ContentVersionRowViewModel row, string instanceId, string instanceName)
    {
        var successText = _localizer.Get("downloads.discovery.completedToInstance", instanceName);
        QueueRowAction(
            row,
            progress => _install.InstallVersionAsync(instanceId, row.Hit, row.Version, progress),
            successText);
    }

    private void StartDirectoryDownload(ContentVersionRowViewModel row, string directory)
    {
        var successText = _localizer.Get("downloads.discovery.completedToDirectory", directory);
        QueueRowAction(
            row,
            progress => _install.DownloadVersionAsync(row.Hit, row.Version, directory, progress),
            successText);
    }

    private void QueueRowAction<T>(
        ContentVersionRowViewModel row,
        Func<IProgress<ContentProgress>?, Task<Result<T>>> action,
        string successText)
    {
        row.MarkQueued();
        StatusMessage = _localizer.Get("downloads.discovery.queued", row.Version.VersionName);
        _downloadTasks.RemoveAll(task => task.IsCompleted);
        _downloadTasks.Add(RunRowActionAsync(row, action, successText));
    }

    private async Task RunRowActionAsync<T>(
        ContentVersionRowViewModel row,
        Func<IProgress<ContentProgress>?, Task<Result<T>>> action,
        string successText)
    {
        await _downloadGate.WaitAsync().ConfigureAwait(true);
        try
        {
            row.MarkDownloading();
            var progress = new Progress<ContentProgress>(row.ApplyProgress);
            var result = await action(progress).ConfigureAwait(true);
            if (result.IsSuccess)
            {
                row.MarkCompleted(successText);
                StatusMessage = successText;
                return;
            }

            var message = result.Error?.Message ?? _localizer.Get("downloads.failed");
            row.MarkFailed(message);
            StatusMessage = _localizer.Get("downloads.discovery.failed", message);
        }
        catch (OperationCanceledException)
        {
            row.MarkFailed(_localizer.Get("downloads.discovery.cancelled"));
        }
        catch (Exception exception)
        {
            row.MarkFailed(exception.Message);
            StatusMessage = _localizer.Get("downloads.discovery.failed", exception.Message);
        }
        finally
        {
            _downloadGate.Release();
        }
    }

    /// <summary>测试与页面切换可以等待条级任务结束；行状态本身仍由领域进度驱动。</summary>
    public async Task WhenDownloadsSettledAsync()
    {
        while (true)
        {
            var pending = _downloadTasks.Where(task => !task.IsCompleted).ToArray();
            if (pending.Length == 0)
            {
                return;
            }

            await Task.WhenAll(pending).ConfigureAwait(true);
        }
    }

    private void ClearResults()
    {
        Results.Clear();
        _hasSearched = false;
        ResultSummary = string.Empty;
        SearchWarnings = string.Empty;
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(HasSearched));
        OnPropertyChanged(nameof(IsEmptyResult));
    }

    private async Task RefreshShaderSupportAsync()
    {
        if (Kind != ContentKind.ShaderPack)
        {
            return;
        }

        if (SelectedInstance?.Id is not { } instanceId)
        {
            ShaderSupportNote = string.Empty;
            return;
        }

        var result = await _shaderSupport.DetectAsync(instanceId).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            ShaderSupportNote = _localizer.Get("downloads.discovery.shaderSupportFailed");
            return;
        }

        var state = ContentDiscoveryPresentation.FormatShaderSupportState(result.Value!.State, _localizer);
        ShaderSupportNote = _localizer.Get("downloads.discovery.shaderSupport", state) +
                            " " +
                            _localizer.Get("downloads.discovery.shaderUnverified");
    }

    partial void OnQueryChanged(string value)
    {
        OnPropertyChanged(nameof(IsSearchInputInvalid));
        if (!IsSearchInputInvalid)
        {
            return;
        }

        StatusMessage = EnglishOnlyHint;
    }

    partial void OnUseExistingInstanceChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowsCustomFilters));
        OnPropertyChanged(nameof(ShowsInstanceSelector));
        OnPropertyChanged(nameof(IsTargetLocked));
        OnPropertyChanged(nameof(ShowNoInstances));
        OnPropertyChanged(nameof(InstanceHint));
        ClearResults();
        StatusMessage = string.Empty;

        if (!value || Kind != ContentKind.Mod)
        {
            return;
        }

        _suppressFilterReset = true;
        try
        {
            SelectedInstance = SelectedInstance ?? TargetInstances.FirstOrDefault();
        }
        finally
        {
            _suppressFilterReset = false;
        }

        OnPropertyChanged(nameof(HasTargetInstance));
        _ = RefreshShaderSupportAsync();
    }

    partial void OnSelectedInstanceChanged(ContentInstanceChoice? value)
    {
        OnPropertyChanged(nameof(HasTargetInstance));
        OnPropertyChanged(nameof(InstanceHint));

        if (_suppressFilterReset)
        {
            return;
        }

        ClearResults();
        StatusMessage = string.Empty;
        _ = RefreshShaderSupportAsync();
    }

    partial void OnSelectedGameVersionChanged(VersionChoiceViewModel? value)
    {
        if (_suppressFilterReset)
        {
            return;
        }

        ClearResults();
        StatusMessage = string.Empty;
    }

    partial void OnSelectedLoaderChanged(LoaderFilterChoice? value)
    {
        if (_suppressFilterReset)
        {
            return;
        }

        ClearResults();
        StatusMessage = string.Empty;
    }

    partial void OnSelectedSourceChanged(ContentSourceChoice? value)
    {
        if (!IsInitialized)
        {
            return;
        }

        if (IsTargetLocked && !HasTargetInstance)
        {
            return;
        }

        _ = SearchAsync();
    }

    partial void OnIsSearchingChanged(bool value) => OnPropertyChanged(nameof(CanSearch));
}

/// <summary>列表一行：图标、名称、简介、版本范围、加载器/适用信息、来源与下载文件大小。</summary>
public sealed class ContentResultRowViewModel
{
    public ContentResultRowViewModel(ContentHit hit, ContentKind kind, ILocalizer localizer)
    {
        Hit = hit;
        Name = hit.Name;
        Summary = string.IsNullOrWhiteSpace(hit.Summary)
            ? localizer.Get("downloads.discovery.noSummary")
            : hit.Summary;
        VersionRangeText = ContentDiscoveryPresentation.FormatVersionRange(hit.GameVersions, localizer);
        LoaderText = kind switch
        {
            ContentKind.Mod or ContentKind.ModPack => ContentDiscoveryPresentation.FormatLoaders(hit, localizer),
            ContentKind.ShaderPack => ContentDiscoveryPresentation.FormatShaderApplicability(hit, localizer),
            _ => string.Empty,
        };
        SourceLabel = hit.SourceLabel;
        SizeText = ContentDiscoveryPresentation.FormatSize(hit.FileSize, localizer);
        IconUrl = hit.IconUrl;
        UpdatedText = ContentDiscoveryPresentation.FormatDate(hit.UpdatedAt, localizer);
    }

    public ContentHit Hit { get; }

    public string Name { get; }

    public string Summary { get; }

    public string VersionRangeText { get; }

    public string LoaderText { get; }

    public bool ShowLoader => LoaderText.Length > 0;

    public string SourceLabel { get; }

    public bool IsModrinth => Hit.Source == ContentSource.Modrinth;

    public string SizeText { get; }

    public string? IconUrl { get; }

    public string UpdatedText { get; }
}

/// <summary>来源筛选选项：全部 / CurseForge / Modrinth（不跨来源合并条目）。</summary>
public sealed record ContentSourceChoice(ContentSourceFilter Filter, string Title)
{
    /// <summary>UIA/无障碍名称只使用本地化标题，不暴露记录类型的 ToString()。</summary>
    public override string ToString() => Title;
}

/// <summary>目标实例；<see cref="Id"/> 为空表示“不选择实例（仅下载）”。</summary>
public sealed record ContentInstanceChoice(string? Id, string Name, string GameVersion, string Loader)
{
    public override string ToString() => Name;
}

/// <summary>加载器筛选；<see cref="Id"/> 为空表示“无”（不按加载器限制）。</summary>
public sealed record LoaderFilterChoice(string? Id, string Title)
{
    public override string ToString() => Title;
}
