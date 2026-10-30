// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.App.Services;
using Ecl.Auth;
using Ecl.Configuration.Settings;
using Ecl.Core;
using Ecl.Core.Localization;
using Ecl.Download;
using Ecl.Java;
using Ecl.Minecraft.Diagnostics;
using Ecl.Minecraft.Eula;
using Ecl.Minecraft.Install;
using Ecl.Minecraft.Instances;
using Ecl.Minecraft.Java;
using Ecl.Minecraft.Launch;
using Ecl.Minecraft.Loaders;
using Ecl.Minecraft.Versions;

namespace Ecl.App.ViewModels;

public sealed partial class InstancesViewModel : ObservableObject
{
    private readonly IInstanceService _instances;
    private readonly IAccountService _accounts;
    private readonly ILaunchService _launchService;
    private readonly IVersionInstallService _installService;
    private readonly ISettingsService _settings;
    private readonly IDialogService _dialogs;
    private readonly ILocalizer _localizer;
    private readonly IAppPaths _paths;
    private readonly IJavaLocator _javaLocator;
    private readonly IJavaSelectionService _javaSelection;
    private readonly IJavaInstallService _javaInstall;
    private readonly IVersionResolver _versionResolver;
    private readonly IEulaService _eula;
    private readonly ILoaderCatalog _loaderCatalog;
    private readonly IDownloadController _downloads;
    private readonly ICrashReportService _crashReports;
    private readonly IDiagnosticPackageService _diagnostics;
    private bool _initialized;
    private bool _suppressAccountSelection;
    private bool _suppressJavaSelection;
    private bool _suppressJavaCheck;

    /// <summary>打开内容管理页时，将目标实例同步到 Mod 管理页。</summary>
    public Action<string>? NavigateToContent { get; set; }

    /// <summary>打开当前实例的独立工作区页面。</summary>
    public Action<string>? NavigateToDetails { get; set; }

    /// <summary>从实例工作区返回实例列表。</summary>
    public Action? NavigateBackFromDetails { get; set; }

    public Func<string, Task<string>>? GetModHealthSummary { get; set; }

    public ObservableCollection<InstanceItemViewModel> Instances { get; } = [];

    public ObservableCollection<AccountItemViewModel> Accounts { get; } = [];

    public ObservableCollection<LaunchStepItemViewModel> LaunchSteps { get; } = [];

    public ObservableCollection<JavaOptionViewModel> JavaOptions { get; } = [];

    public ObservableCollection<LoaderChoiceViewModel> LoaderChoices { get; } = [];

    public ObservableCollection<VersionChoiceViewModel> LoaderVersions { get; } = [];

    public ObservableCollection<VersionChoiceViewModel> FabricApiVersions { get; } = [];

    public ObservableCollection<VersionChoiceViewModel> OptiFineVersions { get; } = [];

    public ObservableCollection<JvmPresetOptionViewModel> InstanceJvmPresetOptions { get; } = [];

    [ObservableProperty]
    private InstanceItemViewModel? _selectedInstance;

    [ObservableProperty]
    private AccountItemViewModel? _selectedAccount;

    [ObservableProperty]
    private string _newInstanceName = string.Empty;

    [ObservableProperty]
    private string _newInstanceVersion = "1.21.1";

    [ObservableProperty]
    private LoaderChoiceViewModel? _selectedNewLoader;

    [ObservableProperty]
    private VersionChoiceViewModel? _selectedLoaderVersion;

    [ObservableProperty]
    private VersionChoiceViewModel? _selectedFabricApiVersion;

    [ObservableProperty]
    private VersionChoiceViewModel? _selectedOptiFineVersion;

    [ObservableProperty]
    private bool _isLoadingComponents;

    [ObservableProperty]
    private string _componentStatus = string.Empty;

    [ObservableProperty]
    private bool _showFabricApiNotice;

    [ObservableProperty]
    private bool _optiFineEnabled;

    [ObservableProperty]
    private string _newAccountName = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private string _launchStatus = string.Empty;

    [ObservableProperty]
    private string _launchCommandPreview = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isInstalling;

    [ObservableProperty]
    private bool _installIndeterminate;

    [ObservableProperty]
    private double _installProgressValue;

    [ObservableProperty]
    private string _installStatus = string.Empty;

    [ObservableProperty]
    private JavaOptionViewModel? _selectedJavaOption;

    [ObservableProperty]
    private string _javaStatus = string.Empty;

    [ObservableProperty]
    private bool _disableJavaCompatibilityCheck;

    [ObservableProperty]
    private string _instanceJvmArguments = string.Empty;

    [ObservableProperty]
    private string _instanceJvmPreset = string.Empty;

    [ObservableProperty]
    private string _instanceMemoryMbText = string.Empty;

    [ObservableProperty]
    private bool _isDownloadingJava;

    [ObservableProperty]
    private bool _javaDownloadIndeterminate;

    [ObservableProperty]
    private double _javaDownloadProgressValue;

    [ObservableProperty]
    private string _javaDownloadStatus = string.Empty;

    [ObservableProperty]
    private string _downloadSpeedText = string.Empty;

    [ObservableProperty]
    private string _downloadPauseButtonText = string.Empty;

    [ObservableProperty]
    private bool _isDownloadPaused;

    [ObservableProperty]
    private bool _canPauseDownload;

    [ObservableProperty]
    private string _crashAnalysisText = string.Empty;

    [ObservableProperty]
    private string _diagnosticStatus = string.Empty;

    [ObservableProperty]
    private string _healthReportText = string.Empty;

    [ObservableProperty]
    private bool _isCheckingHealth;

    [ObservableProperty]
    private bool _isAnalyzingCrash;

    public bool HasSelection => SelectedInstance is not null;

    public bool IsEmpty => Instances.Count == 0;

    public string SelectedInstanceName => SelectedInstance?.Name ?? string.Empty;

    public string SelectedInstanceMeta => SelectedInstance is null
        ? string.Empty
        : $"{SelectedInstance.Model.VersionId} · {SelectedInstance.Model.Loader}";

    public string SelectedInstancePath => SelectedInstance is null
        ? string.Empty
        : _paths.GetInstanceGameDirectory(SelectedInstance.Model.Id);

    public string SelectedInstanceCreated => SelectedInstance is null
        ? string.Empty
        : _localizer.Get("instances.createdAt", SelectedInstance.Model.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd"));

    public string SelectedInstanceComponents
    {
        get
        {
            if (SelectedInstance is null)
            {
                return string.Empty;
            }

            var model = SelectedInstance.Model;
            var parts = new List<string>();
            if (!string.Equals(model.Loader, LoaderKinds.Vanilla, StringComparison.OrdinalIgnoreCase))
            {
                parts.Add(string.IsNullOrWhiteSpace(model.LoaderVersion)
                    ? model.Loader
                    : _localizer.Get("instances.loaderSummary", model.Loader, model.LoaderVersion));
            }

            if (!string.IsNullOrWhiteSpace(model.FabricApiVersion))
            {
                parts.Add($"Fabric API {model.FabricApiVersion}");
            }

            if (!string.IsNullOrWhiteSpace(model.OptiFineVersion))
            {
                parts.Add($"OptiFine {model.OptiFineVersion}");
            }

            return parts.Count == 0
                ? string.Empty
                : _localizer.Get("instances.components.summary", string.Join(" · ", parts));
        }
    }

    public InstancesViewModel(
        IInstanceService instances,
        IAccountService accounts,
        ILaunchService launchService,
        IVersionInstallService installService,
        ISettingsService settings,
        IDialogService dialogs,
        ILocalizer localizer,
        IAppPaths paths,
        IJavaLocator javaLocator,
        IJavaSelectionService javaSelection,
        IJavaInstallService javaInstall,
        IVersionResolver versionResolver,
        IEulaService eula,
        ILoaderCatalog loaderCatalog,
        IDownloadController downloads,
        ICrashReportService crashReports,
        IDiagnosticPackageService diagnostics)
    {
        _instances = instances;
        _accounts = accounts;
        _launchService = launchService;
        _installService = installService;
        _settings = settings;
        _dialogs = dialogs;
        _localizer = localizer;
        _paths = paths;
        _javaLocator = javaLocator;
        _javaSelection = javaSelection;
        _javaInstall = javaInstall;
        _versionResolver = versionResolver;
        _eula = eula;
        _loaderCatalog = loaderCatalog;
        _downloads = downloads;
        _crashReports = crashReports;
        _diagnostics = diagnostics;

        DownloadPauseButtonText = _localizer.Get("instances.download.pause");

        LoaderChoices.Add(new LoaderChoiceViewModel(LoaderKinds.Vanilla, _localizer.Get("instances.loader.vanilla")));
        LoaderChoices.Add(new LoaderChoiceViewModel(LoaderKinds.Fabric, _localizer.Get("instances.loader.fabric")));
        LoaderChoices.Add(new LoaderChoiceViewModel(LoaderKinds.Forge, _localizer.Get("instances.loader.forge")));
        LoaderChoices.Add(new LoaderChoiceViewModel(LoaderKinds.NeoForge, _localizer.Get("instances.loader.neoforge")));
        LoaderChoices.Add(new LoaderChoiceViewModel(LoaderKinds.Cleanroom, _localizer.Get("instances.loader.cleanroom")));
        _selectedNewLoader = LoaderChoices[0];

        InstanceJvmPresetOptions.Add(new JvmPresetOptionViewModel(string.Empty, _localizer.Get("common.inherit")));
        InstanceJvmPresetOptions.Add(new JvmPresetOptionViewModel("default", _localizer.Get("option.java.jvmPreset.default")));
        InstanceJvmPresetOptions.Add(new JvmPresetOptionViewModel("g1", _localizer.Get("option.java.jvmPreset.g1")));
        InstanceJvmPresetOptions.Add(new JvmPresetOptionViewModel("zgc", _localizer.Get("option.java.jvmPreset.zgc")));
        InstanceJvmPresetOptions.Add(new JvmPresetOptionViewModel("aikar", _localizer.Get("option.java.jvmPreset.aikar")));
    }

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        await RefreshAccountsAsync().ConfigureAwait(true);
        await RefreshInstancesAsync().ConfigureAwait(true);
        await RefreshJavaAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await RefreshAccountsAsync().ConfigureAwait(true);
        await RefreshInstancesAsync().ConfigureAwait(true);
        await RefreshJavaAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RefreshJavaAsync()
    {
        _suppressJavaSelection = true;
        try
        {
            var runtimes = await _javaLocator.FindAllAsync().ConfigureAwait(true);
            JavaOptions.Clear();
            JavaOptions.Add(new JavaOptionViewModel(null, _localizer.Get("instances.java.auto")));

            foreach (var runtime in runtimes)
            {
                JavaOptions.Add(new JavaOptionViewModel(runtime, FormatJava(runtime)));
            }

            if (SelectedInstance is not null)
            {
                var configured = _settings.Get<string?>("instance.java.executablePath", SelectedInstance.Model.Id);
                SelectConfiguredJavaOption(configured);
            }
            else
            {
                SelectedJavaOption = JavaOptions[0];
            }
        }
        finally
        {
            _suppressJavaSelection = false;
        }

        await UpdateJavaStatusAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RefreshComponentsAsync()
    {
        ClearComponentChoices();
        ComponentStatus = string.Empty;

        var loader = SelectedNewLoader?.Id ?? LoaderKinds.Vanilla;
        if (loader == LoaderKinds.Vanilla)
        {
            return;
        }

        var version = NewInstanceVersion.Trim();
        if (version.Length == 0)
        {
            ComponentStatus = _localizer.Get("instances.versionRequired");
            return;
        }

        if (loader == LoaderKinds.Cleanroom &&
            !string.Equals(version, "1.12.2", StringComparison.OrdinalIgnoreCase))
        {
            ComponentStatus = _localizer.Get("instances.loader.cleanroomOnly");
            return;
        }

        IsLoadingComponents = true;
        ComponentStatus = _localizer.Get("instances.components.loading");

        try
        {
            if (loader == LoaderKinds.Fabric)
            {
                var loaders = await _loaderCatalog.GetFabricLoadersAsync(version).ConfigureAwait(true);
                if (!loaders.IsSuccess)
                {
                    ComponentStatus = _localizer.Get("instances.components.failed", loaders.Error?.Message ?? string.Empty);
                    return;
                }

                foreach (var item in loaders.Value!)
                {
                    LoaderVersions.Add(FormatLoaderVersion(item));
                }

                SelectedLoaderVersion = LoaderVersions.FirstOrDefault();

                var apiVersions = await _loaderCatalog.GetFabricApiVersionsAsync(version).ConfigureAwait(true);
                if (!apiVersions.IsSuccess)
                {
                    ComponentStatus = _localizer.Get("instances.components.failed", apiVersions.Error?.Message ?? string.Empty);
                    return;
                }

                foreach (var item in apiVersions.Value!)
                {
                    FabricApiVersions.Add(new VersionChoiceViewModel(
                        item.Version,
                        FormatVersionWithDate(item.Version, item.ReleasedAt)));
                }

                SelectedFabricApiVersion = FabricApiVersions.FirstOrDefault();
                ComponentStatus = LoaderVersions.Count == 0
                    ? _localizer.Get("instances.loader.noVersions")
                    : FabricApiVersions.Count == 0
                        ? _localizer.Get("instances.fabricApi.none")
                        : string.Empty;
                return;
            }

            if ((loader == LoaderKinds.Forge || loader == LoaderKinds.NeoForge) &&
                LoaderKinds.TryParse(loader, out var installerKind))
            {
                var loaders = installerKind == LoaderKind.Forge
                    ? await _loaderCatalog.GetForgeVersionsAsync(version).ConfigureAwait(true)
                    : await _loaderCatalog.GetNeoForgeVersionsAsync(version).ConfigureAwait(true);

                if (!loaders.IsSuccess)
                {
                    ComponentStatus = _localizer.Get("instances.components.failed", loaders.Error?.Message ?? string.Empty);
                    return;
                }

                foreach (var item in loaders.Value!)
                {
                    LoaderVersions.Add(FormatLoaderVersion(item));
                }

                SelectedLoaderVersion = LoaderVersions.FirstOrDefault();

                if (installerKind == LoaderKind.Forge)
                {
                    var forgeOptiFineVersions = await _loaderCatalog.GetOptiFineVersionsAsync(version).ConfigureAwait(true);
                    if (!forgeOptiFineVersions.IsSuccess)
                    {
                        ComponentStatus = _localizer.Get("instances.components.failed", forgeOptiFineVersions.Error?.Message ?? string.Empty);
                        return;
                    }

                    OptiFineVersions.Add(new VersionChoiceViewModel(null, _localizer.Get("instances.optifine.none")));
                    foreach (var item in forgeOptiFineVersions.Value!)
                    {
                        OptiFineVersions.Add(new VersionChoiceViewModel(
                            item.Key,
                            FormatVersionWithDate(item.FileName, item.ReleasedAt)));
                    }

                    SelectedOptiFineVersion = OptiFineVersions[0];
                }

                ComponentStatus = LoaderVersions.Count == 0
                    ? _localizer.Get("instances.loader.noVersions")
                    : string.Empty;
                return;
            }

            var cleanroomVersions = await _loaderCatalog.GetCleanroomVersionsAsync(version).ConfigureAwait(true);
            if (!cleanroomVersions.IsSuccess)
            {
                ComponentStatus = _localizer.Get("instances.components.failed", cleanroomVersions.Error?.Message ?? string.Empty);
                return;
            }

            foreach (var item in cleanroomVersions.Value!)
            {
                LoaderVersions.Add(FormatLoaderVersion(item));
            }

            SelectedLoaderVersion = LoaderVersions.FirstOrDefault();

            var optiFineVersions = await _loaderCatalog.GetOptiFineVersionsAsync(version).ConfigureAwait(true);
            if (!optiFineVersions.IsSuccess)
            {
                ComponentStatus = _localizer.Get("instances.components.failed", optiFineVersions.Error?.Message ?? string.Empty);
                return;
            }

            OptiFineVersions.Add(new VersionChoiceViewModel(null, _localizer.Get("instances.optifine.none")));
            foreach (var item in optiFineVersions.Value!)
            {
                OptiFineVersions.Add(new VersionChoiceViewModel(
                    item.Key,
                    FormatVersionWithDate(item.FileName, item.ReleasedAt)));
            }

            SelectedOptiFineVersion = OptiFineVersions[0];
            ComponentStatus = LoaderVersions.Count == 0
                ? _localizer.Get("instances.loader.noVersions")
                : string.Empty;
        }
        catch (Exception exception)
        {
            ComponentStatus = _localizer.Get("instances.components.failed", exception.Message);
        }
        finally
        {
            IsLoadingComponents = false;
        }
    }

    private void ClearComponentChoices()
    {
        LoaderVersions.Clear();
        FabricApiVersions.Clear();
        OptiFineVersions.Clear();
        SelectedLoaderVersion = null;
        SelectedFabricApiVersion = null;
        SelectedOptiFineVersion = null;
    }

    private VersionChoiceViewModel FormatLoaderVersion(LoaderVersionInfo version) =>
        new(version.Version, FormatVersionWithDate(version.Version, version.ReleasedAt));

    private string FormatVersionWithDate(string version, DateTimeOffset? releasedAt)
    {
        var date = releasedAt?.ToLocalTime().ToString("yyyy-MM-dd")
                   ?? _localizer.Get("instances.versionDateUnknown");
        return _localizer.Get("instances.versionWithDate", version, date);
    }

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
    private async Task DownloadJavaAsync()
    {
        if (SelectedInstance is null)
        {
            StatusMessage = _localizer.Get("instances.selectFirst");
            return;
        }

        if (IsBusy)
        {
            return;
        }

        var instance = SelectedInstance.Model;
        var versionResult = await _versionResolver.ResolveAsync(instance.VersionId).ConfigureAwait(true);

        // 新建 Forge/Cleanroom 实例时版本 JSON 可能尚未写入；此时按 GameVersion 推断
        // Java 要求，避免“先有版本 JSON 才能下载 Java、先有 Java 才能安装加载器”的死循环。
        var version = versionResult.IsSuccess ? versionResult.Value : null;
        var requirement = JavaRequirements.Resolve(version, instance.GameVersion);
        if (!versionResult.IsSuccess)
        {
            JavaDownloadStatus = _localizer.Get("instances.java.usingGameVersion", instance.GameVersion);
        }

        IsBusy = true;
        IsDownloadingJava = true;
        JavaDownloadIndeterminate = true;
        JavaDownloadProgressValue = 0;
        JavaDownloadStatus = _localizer.Get("instances.java.downloading");
        BeginDownloadUi();

        var progress = new Progress<JavaInstallProgress>(update =>
        {
            JavaDownloadIndeterminate = update.IsIndeterminate;
            JavaDownloadProgressValue = update.Percent;
            JavaDownloadStatus = update.TotalFiles > 0
                ? $"{update.Message} ({update.CompletedFiles}/{update.TotalFiles})"
                : update.Message;
            if (update.BytesPerSecond > 0)
            {
                DownloadSpeedText = FormatSpeed(update.BytesPerSecond);
            }
        });

        try
        {
            var result = await _javaInstall
                .EnsureAsync(requirement, progress)
                .ConfigureAwait(true);

            if (!result.IsSuccess)
            {
                JavaDownloadStatus = result.Error?.Message ?? _localizer.Get("instances.java.downloadFailed");
                StatusMessage = JavaDownloadStatus;
                return;
            }

            JavaDownloadStatus = _localizer.Get("instances.java.downloadSucceeded");
            await RefreshJavaAsync().ConfigureAwait(true);

            if (result.Value is not null)
            {
                var option = JavaOptions.FirstOrDefault(item =>
                    item.Runtime is not null &&
                    string.Equals(item.Runtime.ExecutablePath, result.Value.ExecutablePath, StringComparison.OrdinalIgnoreCase));

                if (option is not null)
                {
                    _suppressJavaSelection = true;
                    SelectedJavaOption = option;
                    _suppressJavaSelection = false;
                    _settings.TrySetRaw("instance.java.executablePath", result.Value.ExecutablePath, instance.Id, out _);
                }
            }

            await UpdateJavaStatusAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            JavaDownloadStatus = _localizer.Get("instances.java.downloadFailed");
            StatusMessage = exception.Message;
        }
        finally
        {
            IsDownloadingJava = false;
            IsBusy = false;
            EndDownloadUi();
        }
    }

    [RelayCommand]
    private async Task CreateInstanceAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var name = NewInstanceName.Trim();
        var version = NewInstanceVersion.Trim();
        if (name.Length == 0)
        {
            StatusMessage = _localizer.Get("instances.nameRequired");
            return;
        }

        if (version.Length == 0)
        {
            StatusMessage = _localizer.Get("instances.versionRequired");
            return;
        }

        var loader = SelectedNewLoader?.Id ?? LoaderKinds.Vanilla;
        if (loader == LoaderKinds.Cleanroom &&
            !string.Equals(version, "1.12.2", StringComparison.OrdinalIgnoreCase))
        {
            StatusMessage = _localizer.Get("instances.loader.cleanroomOnly");
            return;
        }

        string? loaderVersion = null;
        string? fabricApiVersion = null;
        string? optiFineVersion = null;

        if (loader != LoaderKinds.Vanilla)
        {
            loaderVersion = SelectedLoaderVersion?.Value;
            if (string.IsNullOrWhiteSpace(loaderVersion))
            {
                StatusMessage = _localizer.Get("instances.loader.versionRequired");
                return;
            }
        }

        if (loader == LoaderKinds.Fabric)
        {
            fabricApiVersion = SelectedFabricApiVersion?.Value;
            if (string.IsNullOrWhiteSpace(fabricApiVersion))
            {
                StatusMessage = _localizer.Get("instances.fabricApi.none");
                return;
            }
        }

        if (loader == LoaderKinds.Cleanroom || loader == LoaderKinds.Forge)
        {
            optiFineVersion = SelectedOptiFineVersion?.Value;
        }

        IsBusy = true;
        try
        {
            var result = await _instances
                .CreateAsync(name, version, loader, loaderVersion, fabricApiVersion, optiFineVersion)
                .ConfigureAwait(true);

            if (!result.IsSuccess)
            {
                StatusMessage = result.Error?.Message ?? string.Empty;
                return;
            }

            NewInstanceName = string.Empty;
            await RefreshInstancesAsync().ConfigureAwait(true);
            SelectedInstance = Instances.FirstOrDefault(item => item.Model.Id == result.Value!.Id) ?? Instances.FirstOrDefault();
            StatusMessage = _localizer.Get("instances.created", result.Value!.Name);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DeleteInstanceAsync()
    {
        if (SelectedInstance is null || IsBusy)
        {
            return;
        }

        var instance = SelectedInstance;
        if (_settings.Get<bool>("launcher.confirmBeforeDelete") &&
            !_dialogs.Confirm(
                _localizer.Get("instances.deleteConfirmTitle"),
                _localizer.Get("instances.deleteConfirm", instance.Name)))
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _instances.DeleteAsync(instance.Model.Id).ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                StatusMessage = result.Error?.Message ?? string.Empty;
                return;
            }

            await RefreshInstancesAsync().ConfigureAwait(true);
            StatusMessage = _localizer.Get("instances.deleted", instance.Name);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private Task LaunchAsync() => RunLaunchAsync(dryRun: false);

    [RelayCommand]
    private Task DryRunAsync() => RunLaunchAsync(dryRun: true);

    [RelayCommand]
    private async Task AnalyzeCrashAsync()
    {
        if (SelectedInstance is null)
        {
            StatusMessage = _localizer.Get("instances.selectFirst");
            return;
        }

        if (IsAnalyzingCrash)
        {
            return;
        }

        IsAnalyzingCrash = true;
        CrashAnalysisText = _localizer.Get("diagnostics.analyzing");
        try
        {
            var result = await _crashReports
                .AnalyzeLatestAsync(SelectedInstance.Model.Id)
                .ConfigureAwait(true);

            if (!result.IsSuccess)
            {
                CrashAnalysisText = result.Error?.ToString() ?? _localizer.Get("diagnostics.failed");
                return;
            }

            if (result.Value is null)
            {
                CrashAnalysisText = _localizer.Get("diagnostics.noCrash");
                return;
            }

            var analysis = result.Value;
            var builder = new System.Text.StringBuilder();
            builder.AppendLine(analysis.Summary);
            if (!string.IsNullOrWhiteSpace(analysis.SourcePath))
            {
                builder.AppendLine(_localizer.Get("diagnostics.source", analysis.SourcePath));
            }

            if (analysis.Suspects.Count > 0)
            {
                builder.AppendLine(_localizer.Get("diagnostics.suspects"));
                foreach (var suspect in analysis.Suspects)
                {
                    builder.AppendLine($"  {suspect.Name ?? suspect.ModId} ({suspect.ModId}) · {suspect.Score}");
                    foreach (var evidence in suspect.Evidence.Take(2))
                    {
                        builder.AppendLine($"    {evidence}");
                    }
                }
            }

            if (analysis.KeyLines.Count > 0)
            {
                builder.AppendLine(_localizer.Get("diagnostics.keyLines"));
                foreach (var line in analysis.KeyLines.Take(6))
                {
                    builder.AppendLine($"  {line}");
                }
            }

            CrashAnalysisText = builder.ToString().TrimEnd();
        }
        catch (Exception exception)
        {
            CrashAnalysisText = exception.Message;
        }
        finally
        {
            IsAnalyzingCrash = false;
        }
    }

    [RelayCommand]
    private async Task ExportDiagnosticsAsync()
    {
        if (SelectedInstance is null)
        {
            StatusMessage = _localizer.Get("instances.selectFirst");
            return;
        }

        DiagnosticStatus = _localizer.Get("diagnostics.exporting");
        try
        {
            var result = await _diagnostics
                .ExportAsync(SelectedInstance.Model.Id)
                .ConfigureAwait(true);

            DiagnosticStatus = result.IsSuccess
                ? _localizer.Get("diagnostics.exported", result.Value!.OutputPath, result.Value.Entries)
                : result.Error?.ToString() ?? _localizer.Get("diagnostics.exportFailed");
        }
        catch (Exception exception)
        {
            DiagnosticStatus = exception.Message;
        }
    }

    [RelayCommand]
    private async Task RepairFilesAsync()
    {
        if (SelectedInstance is null)
        {
            StatusMessage = _localizer.Get("instances.selectFirst");
            return;
        }

        if (!_dialogs.Confirm(
                _localizer.Get("instances.repairTitle"),
                _localizer.Get("instances.repairConfirm", SelectedInstance.Name)))
        {
            return;
        }

        await InstallAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task CheckHealthAsync()
    {
        if (SelectedInstance is null)
        {
            StatusMessage = _localizer.Get("instances.selectFirst");
            return;
        }

        if (IsBusy || IsCheckingHealth)
        {
            return;
        }

        IsCheckingHealth = true;
        HealthReportText = _localizer.Get("instances.healthChecking");
        try
        {
            var report = await _launchService
                .LaunchAsync(SelectedInstance.Model.Id, dryRun: true)
                .ConfigureAwait(true);
            LaunchSteps.Clear();
            foreach (var step in report.Steps)
            {
                LaunchSteps.Add(new LaunchStepItemViewModel(step, _localizer));
            }

            LaunchCommandPreview = report.CommandLine ?? string.Empty;
            LaunchStatus = _localizer.Get(report.Success ? "instances.healthLaunchReady" : "instances.healthLaunchIssues");
            var modSummary = GetModHealthSummary is null
                ? _localizer.Get("instances.healthModsUnavailable")
                : await GetModHealthSummary(SelectedInstance.Model.Id).ConfigureAwait(true);
            HealthReportText = $"{LaunchStatus}{Environment.NewLine}{modSummary}";
        }
        catch (Exception exception)
        {
            HealthReportText = _localizer.Get("instances.healthFailed", exception.Message);
        }
        finally
        {
            IsCheckingHealth = false;
        }
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        if (SelectedInstance is null)
        {
            StatusMessage = _localizer.Get("instances.selectFirst");
            return;
        }

        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        IsInstalling = true;
        InstallIndeterminate = true;
        InstallProgressValue = 0;
        InstallStatus = _localizer.Get("instances.installing");
        BeginDownloadUi();

        var progress = new Progress<InstallProgress>(update =>
        {
            InstallIndeterminate = update.IsIndeterminate;
            InstallProgressValue = update.Percent;
            InstallStatus = update.TotalFiles > 0
                ? $"{update.Message} ({update.CompletedFiles}/{update.TotalFiles})"
                : update.Message;
            if (update.BytesPerSecond > 0)
            {
                DownloadSpeedText = FormatSpeed(update.BytesPerSecond);
            }
        });

        try
        {
            var result = await _installService
                .InstallAsync(SelectedInstance.Model.Id, progress)
                .ConfigureAwait(true);

            if (!result.IsSuccess)
            {
                InstallStatus = result.Error?.Message ?? _localizer.Get("instances.installFailed");
                StatusMessage = InstallStatus;
                return;
            }

            InstallIndeterminate = false;
            InstallProgressValue = 100;
            InstallStatus = _localizer.Get("instances.installSucceeded");
            StatusMessage = InstallStatus;
            await UpdateJavaStatusAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            InstallStatus = _localizer.Get("instances.installFailed");
            StatusMessage = _localizer.Get("install.failed", exception.Message);
        }
        finally
        {
            IsInstalling = false;
            IsBusy = false;
            EndDownloadUi();
        }
    }

    [RelayCommand]
    private async Task AddAccountAsync()
    {
        var name = NewAccountName.Trim();
        if (name.Length == 0)
        {
            StatusMessage = _localizer.Get("instances.accountNameRequired");
            return;
        }

        var result = await _accounts.AddOfflineAsync(name, makeDefault: Accounts.Count == 0).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            StatusMessage = result.Error?.Message ?? string.Empty;
            return;
        }

        NewAccountName = string.Empty;
        await RefreshAccountsAsync().ConfigureAwait(true);
        if (result.Value is not null)
        {
            SelectedAccount = Accounts.FirstOrDefault(item => item.Id == result.Value.Id) ?? SelectedAccount;
        }

        StatusMessage = _localizer.Get("instances.accountAdded", result.Value!.Name);
    }

    partial void OnSelectedNewLoaderChanged(LoaderChoiceViewModel? value)
    {
        ShowFabricApiNotice = string.Equals(value?.Id, LoaderKinds.Fabric, StringComparison.OrdinalIgnoreCase);
        OptiFineEnabled = string.Equals(value?.Id, LoaderKinds.Cleanroom, StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(value?.Id, LoaderKinds.Forge, StringComparison.OrdinalIgnoreCase);

        if (!_initialized)
        {
            ClearComponentChoices();
            return;
        }

        _ = RefreshComponentsAsync();
    }

    [RelayCommand]
    private void BrowseJava()
    {
        if (SelectedInstance is null)
        {
            return;
        }

        var path = _dialogs.PickOpenFile(
            "选择此实例使用的 Java",
            "Java 可执行文件 (java.exe)|java.exe|可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*");
        if (path is null)
        {
            return;
        }

        var runtime = _javaLocator.FromPath(path);
        if (runtime is null)
        {
            StatusMessage = "所选文件无法识别为有效的 Java 运行时。";
            return;
        }

        var option = JavaOptions.FirstOrDefault(item =>
            string.Equals(item.Runtime?.ExecutablePath, runtime.ExecutablePath, StringComparison.OrdinalIgnoreCase));
        if (option is null)
        {
            option = new JavaOptionViewModel(runtime, FormatJava(runtime));
            JavaOptions.Add(option);
        }

        SelectedJavaOption = option;
        StatusMessage = $"已为当前实例选择 {runtime.DisplayName}。";
    }

    [RelayCommand]
    private void SaveInstanceRuntimeSettings()
    {
        if (SelectedInstance is null)
        {
            return;
        }

        var memoryText = InstanceMemoryMbText.Trim();
        if (memoryText.Length > 0 &&
            (!int.TryParse(memoryText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var memoryMb) ||
             memoryMb is < 1024 or > 65536))
        {
            StatusMessage = "实例内存必须是 1024 至 65536 MB 之间的整数；留空则继承全局设置。";
            return;
        }

        var instanceId = SelectedInstance.Model.Id;
        if (!_settings.TrySetRaw("instance.java.extraArguments", InstanceJvmArguments, instanceId, out var jvmError))
        {
            StatusMessage = jvmError ?? "保存实例 JVM 参数失败。";
            return;
        }

        if (!_settings.TrySetRaw("instance.java.jvmPreset", InstanceJvmPreset, instanceId, out var presetError))
        {
            StatusMessage = presetError ?? "保存实例 JVM 预设失败。";
            return;
        }

        if (!_settings.TrySetRaw("instance.java.memoryMb", memoryText, instanceId, out var memoryError))
        {
            StatusMessage = memoryError ?? "保存实例内存设置失败。";
            return;
        }

        StatusMessage = "实例运行设置已保存。";
    }

    private void LoadInstanceRuntimeSettings()
    {
        if (SelectedInstance is null)
        {
            InstanceJvmArguments = string.Empty;
            InstanceJvmPreset = string.Empty;
            InstanceMemoryMbText = string.Empty;
            return;
        }

        var instanceId = SelectedInstance.Model.Id;
        InstanceJvmArguments = _settings.Get<string?>("instance.java.extraArguments", instanceId) ?? string.Empty;
        InstanceJvmPreset = _settings.Get<string?>("instance.java.jvmPreset", instanceId) ?? string.Empty;
        InstanceMemoryMbText = _settings.Get<int?>("instance.java.memoryMb", instanceId)?
            .ToString(CultureInfo.InvariantCulture) ?? string.Empty;
    }

    [RelayCommand]
    private void OpenInstanceDetails()
    {
        if (SelectedInstance is not null)
        {
            NavigateToDetails?.Invoke(SelectedInstance.Model.Id);
        }
    }

    [RelayCommand]
    private void BackToInstances() => NavigateBackFromDetails?.Invoke();

    [RelayCommand]
    private void OpenInstanceFolder() => OpenSelectedDirectory(
        SelectedInstance is null ? null : _paths.GetInstanceDirectory(SelectedInstance.Model.Id));

    [RelayCommand]
    private void OpenModsFolder() => OpenSelectedDirectory(
        SelectedInstance is null ? null : Path.Combine(_paths.GetInstanceGameDirectory(SelectedInstance.Model.Id), "mods"));

    [RelayCommand]
    private void ManageInstanceMods()
    {
        if (SelectedInstance is not null)
        {
            NavigateToContent?.Invoke(SelectedInstance.Model.Id);
        }
    }

    [RelayCommand]
    private void ExportLatestLog()
    {
        if (SelectedInstance is null)
        {
            return;
        }

        var source = Path.Combine(_paths.GetInstanceGameDirectory(SelectedInstance.Model.Id), "logs", "latest.log");
        if (!File.Exists(source))
        {
            StatusMessage = "该实例尚无 latest.log；请先运行一次游戏。";
            return;
        }

        if (!_dialogs.Confirm(
                "导出运行日志",
                "latest.log 可能包含服务器地址、玩家名称或其他运行环境信息。请确认目标文件仅分享给可信任的人。"))
        {
            return;
        }

        var destination = _dialogs.PickSaveFile(
            "导出最近一次运行日志",
            "日志文件 (*.log)|*.log|所有文件 (*.*)|*.*",
            $"{SelectedInstance.Name}-latest.log");
        if (destination is null)
        {
            return;
        }

        try
        {
            File.Copy(source, destination, overwrite: true);
            StatusMessage = $"日志已导出：{destination}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"导出日志失败：{exception.Message}";
        }
    }

    private void OpenSelectedDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            StatusMessage = $"无法打开目录：{exception.Message}";
        }
    }

    partial void OnSelectedInstanceChanged(InstanceItemViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedInstanceName));
        OnPropertyChanged(nameof(SelectedInstanceMeta));
        OnPropertyChanged(nameof(SelectedInstancePath));
        OnPropertyChanged(nameof(SelectedInstanceCreated));
        OnPropertyChanged(nameof(SelectedInstanceComponents));

        LaunchSteps.Clear();
        LaunchStatus = string.Empty;
        LaunchCommandPreview = string.Empty;
        InstallStatus = string.Empty;
        InstallProgressValue = 0;
        InstallIndeterminate = false;
        JavaDownloadStatus = string.Empty;

        LoadInstanceRuntimeSettings();
        if (JavaOptions.Count > 0)
        {
            _ = RefreshJavaSelectionAsync();
        }
    }

    partial void OnSelectedJavaOptionChanged(JavaOptionViewModel? value)
    {
        if (_suppressJavaSelection || SelectedInstance is null)
        {
            return;
        }

        var path = value?.Runtime?.ExecutablePath ?? string.Empty;
        if (!_settings.TrySetRaw("instance.java.executablePath", path, SelectedInstance.Model.Id, out var error))
        {
            StatusMessage = error ?? string.Empty;
            return;
        }

        _ = UpdateJavaStatusAsync();
    }

    partial void OnDisableJavaCompatibilityCheckChanged(bool value)
    {
        if (_suppressJavaCheck || SelectedInstance is null)
        {
            return;
        }

        _settings.TrySetRaw(
            "instance.java.disableCompatibilityCheck",
            value ? "true" : "false",
            SelectedInstance.Model.Id,
            out var error);

        if (error is not null)
        {
            StatusMessage = error;
        }

        _ = UpdateJavaStatusAsync();
    }

    partial void OnSelectedAccountChanged(AccountItemViewModel? value)
    {
        if (_suppressAccountSelection || value is null || value.IsDefault)
        {
            return;
        }

        _ = SetDefaultAccountAsync(value.Id);
    }

    private bool EnsureEulaConsent()
    {
        if (_eula.HasAccepted)
        {
            return true;
        }

        if (!_dialogs.Confirm(
                _localizer.Get("eula.title"),
                _localizer.Get("eula.prompt")))
        {
            return false;
        }

        var result = _eula.Accept();
        if (!result.IsSuccess)
        {
            _dialogs.ShowError(
                _localizer.Get("eula.saveFailedTitle"),
                result.Error?.Message ?? string.Empty);
            return false;
        }

        _dialogs.ShowInfo(
            _localizer.Get("eula.noticeTitle"),
            _localizer.Get("eula.multiplayerNotice"));

        return true;
    }

    private async Task<bool> ConfirmJavaBeforeLaunchAsync()
    {
        if (SelectedInstance is null)
        {
            return false;
        }

        var instance = SelectedInstance.Model;
        var versionResult = await _versionResolver.ResolveAsync(instance.VersionId).ConfigureAwait(true);
        if (!versionResult.IsSuccess)
        {
            return true;
        }

        var selection = await _javaSelection
            .ResolveAsync(instance, versionResult.Value)
            .ConfigureAwait(true);

        if (selection.Runtime is null)
        {
            var shouldDownload = _dialogs.Confirm(
                _localizer.Get("instances.java.downloadTitle"),
                _localizer.Get("instances.java.downloadPrompt"));

            if (shouldDownload)
            {
                await DownloadJavaAsync().ConfigureAwait(true);
            }

            return false;
        }

        if (!selection.CompatibilityCheckEnabled ||
            selection.Compatibility is null ||
            selection.Compatibility.IsCompatible)
        {
            return true;
        }

        var actual = selection.Runtime.Version?.Raw ?? selection.Runtime.DisplayName;
        var required = selection.Requirement?.Describe() ?? "Java";
        return _dialogs.Confirm(
            _localizer.Get("instances.java.incompatibleTitle"),
            _localizer.Get("instances.java.incompatiblePrompt", actual, required));
    }

    private void SelectConfiguredJavaOption(string? configuredPath)
    {
        var option = JavaOptions.FirstOrDefault(item =>
            item.Runtime is not null &&
            string.Equals(item.Runtime.ExecutablePath, configuredPath, StringComparison.OrdinalIgnoreCase));

        if (option is null && !string.IsNullOrWhiteSpace(configuredPath))
        {
            var runtime = _javaLocator.FromPath(configuredPath);
            if (runtime is not null)
            {
                option = new JavaOptionViewModel(runtime, FormatJava(runtime));
                JavaOptions.Add(option);
            }
        }

        SelectedJavaOption = option ?? JavaOptions.FirstOrDefault();
    }

    private async Task RefreshJavaSelectionAsync()
    {
        if (SelectedInstance is null)
        {
            JavaStatus = string.Empty;
            return;
        }

        try
        {
            var instance = SelectedInstance.Model;
            _suppressJavaSelection = true;
            var configured = _settings.Get<string?>("instance.java.executablePath", instance.Id);
            SelectConfiguredJavaOption(configured);
            _suppressJavaSelection = false;

            _suppressJavaCheck = true;
            DisableJavaCompatibilityCheck = _settings.Get<bool>("instance.java.disableCompatibilityCheck", instance.Id);
            _suppressJavaCheck = false;

            await UpdateJavaStatusAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            JavaStatus = exception.Message;
        }
    }

    private async Task UpdateJavaStatusAsync()
    {
        if (SelectedInstance is null)
        {
            JavaStatus = string.Empty;
            return;
        }

        try
        {
            var instance = SelectedInstance.Model;
            var versionResult = await _versionResolver.ResolveAsync(instance.VersionId).ConfigureAwait(true);
            if (!versionResult.IsSuccess)
            {
                JavaStatus = _localizer.Get("instances.java.installFirst");
                return;
            }

            var selection = await _javaSelection
                .ResolveAsync(instance, versionResult.Value)
                .ConfigureAwait(true);

            _suppressJavaCheck = true;
            DisableJavaCompatibilityCheck = !selection.CompatibilityCheckEnabled;
            _suppressJavaCheck = false;

            if (selection.Runtime is null)
            {
                JavaStatus = WithWarnings(_localizer.Get("instances.java.notFound"));
                return;
            }

            if (!selection.CompatibilityCheckEnabled)
            {
                JavaStatus = WithWarnings(
                    _localizer.Get("instances.java.checkDisabled", selection.Runtime.DisplayName));
                return;
            }

            if (selection.Compatibility is null || selection.Compatibility.IsCompatible)
            {
                JavaStatus = WithWarnings(
                    _localizer.Get("instances.java.compatible", selection.Runtime.DisplayName));
                return;
            }

            if (selection.Compatibility.Status == JavaCompatibilityStatus.Unknown)
            {
                JavaStatus = WithWarnings(
                    _localizer.Get("instances.java.unknownVersion", selection.Runtime.DisplayName));
                return;
            }

            JavaStatus = WithWarnings(_localizer.Get(
                "instances.java.incompatible",
                selection.Runtime.Version?.Raw ?? selection.Runtime.DisplayName,
                selection.Requirement?.Describe() ?? "Java"));

            string WithWarnings(string text) => selection.Warnings.Count == 0
                ? text
                : $"{text} {string.Join(' ', selection.Warnings)}";
        }
        catch (Exception exception)
        {
            JavaStatus = exception.Message;
        }
    }

    private static string FormatJava(JavaRuntime runtime)
    {
        var version = runtime.Version?.Raw ?? "?";
        var vendor = runtime.Vendor ?? "Java";
        var folder = Path.GetFileName(runtime.HomePath);
        return $"{version} · {vendor} · {folder}";
    }

    private async Task RunLaunchAsync(bool dryRun)
    {
        if (SelectedInstance is null)
        {
            StatusMessage = _localizer.Get("instances.selectFirst");
            return;
        }

        if (IsBusy)
        {
            return;
        }

        if (!dryRun && !EnsureEulaConsent())
        {
            return;
        }

        if (!dryRun && !await ConfirmJavaBeforeLaunchAsync().ConfigureAwait(true))
        {
            return;
        }

        IsBusy = true;
        LaunchSteps.Clear();
        LaunchCommandPreview = string.Empty;
        LaunchStatus = _localizer.Get("instances.launching");

        try
        {
            var report = await _launchService.LaunchAsync(SelectedInstance.Model.Id, dryRun).ConfigureAwait(true);
            foreach (var step in report.Steps)
            {
                LaunchSteps.Add(new LaunchStepItemViewModel(step, _localizer));
            }

            LaunchCommandPreview = report.CommandLine ?? string.Empty;
            LaunchStatus = report.Success
                ? _localizer.Get(dryRun ? "instances.dryRunSucceeded" : "instances.launchSucceeded")
                : _localizer.Get("instances.launchFailed");
        }
        catch (Exception exception)
        {
            LaunchStatus = _localizer.Get("instances.launchException", exception.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshInstancesAsync()
    {
        var selectedId = SelectedInstance?.Model.Id;
        var instances = await _instances.GetInstancesAsync().ConfigureAwait(true);

        Instances.Clear();
        foreach (var instance in instances)
        {
            Instances.Add(new InstanceItemViewModel(instance, _localizer));
        }

        SelectedInstance = Instances.FirstOrDefault(item => item.Model.Id == selectedId) ?? Instances.FirstOrDefault();
        OnPropertyChanged(nameof(IsEmpty));
    }

    private async Task RefreshAccountsAsync()
    {
        _suppressAccountSelection = true;
        try
        {
            var accounts = await _accounts.GetAccountsAsync().ConfigureAwait(true);
            var defaultAccount = await _accounts.GetDefaultAccountAsync().ConfigureAwait(true);

            Accounts.Clear();
            foreach (var account in accounts)
            {
                Accounts.Add(new AccountItemViewModel(account, account.Id == defaultAccount?.Id, _localizer));
            }

            SelectedAccount = Accounts.FirstOrDefault(item => item.IsDefault) ?? Accounts.FirstOrDefault();
        }
        finally
        {
            _suppressAccountSelection = false;
        }
    }

    private async Task SetDefaultAccountAsync(string accountId)
    {
        try
        {
            var result = await _accounts.SetDefaultAsync(accountId).ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                StatusMessage = result.Error?.Message ?? string.Empty;
                return;
            }

            foreach (var account in Accounts)
            {
                account.IsDefault = account.Id == accountId;
            }
        }
        catch (Exception exception)
        {
            StatusMessage = _localizer.Get("instances.launchException", exception.Message);
        }
    }
}

public sealed class InstanceItemViewModel
{
    public InstanceItemViewModel(GameInstance model, ILocalizer localizer)
    {
        Model = model;
        Name = model.Name;
        VersionText = string.Equals(model.Loader, "vanilla", StringComparison.OrdinalIgnoreCase)
            ? model.VersionId
            : $"{model.VersionId} · {model.Loader}";
        LastPlayedText = model.LastPlayedAt is null
            ? localizer.Get("instances.never")
            : localizer.Get("instances.lastPlayedAt", model.LastPlayedAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
    }

    public GameInstance Model { get; }

    public string Name { get; }

    public string VersionText { get; }

    public string LastPlayedText { get; }
}

public sealed partial class AccountItemViewModel : ObservableObject
{
    public AccountItemViewModel(Account model, bool isDefault, ILocalizer localizer)
    {
        Model = model;
        Id = model.Id;
        IsDefault = isDefault;
        DisplayName = $"{model.Name} · {localizer.Get("accounts.offline")}";
    }

    public Account Model { get; }

    public string Id { get; }

    public string DisplayName { get; }

    [ObservableProperty]
    private bool _isDefault;
}

public sealed record JavaOptionViewModel(JavaRuntime? Runtime, string DisplayName);

public sealed record JvmPresetOptionViewModel(string Value, string DisplayName);

public sealed record LoaderChoiceViewModel(string Id, string DisplayName);

public sealed record VersionChoiceViewModel(string? Value, string DisplayName);

public sealed class LaunchStepItemViewModel
{
    public LaunchStepItemViewModel(LaunchStepResult step, ILocalizer localizer)
    {
        Status = step.Status;
        Title = localizer.Get(step.TitleKey);
        Message = step.Message;
        StatusText = localizer.Get($"launch.status.{step.Status.ToString().ToLowerInvariant()}");
        DurationText = $"{step.Duration.TotalMilliseconds:F0} ms";
    }

    public LaunchStepStatus Status { get; }

    public string Title { get; }

    public string Message { get; }

    public string StatusText { get; }

    public string DurationText { get; }
}
