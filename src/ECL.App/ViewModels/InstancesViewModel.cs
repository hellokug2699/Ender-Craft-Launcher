// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.App.Services;
using Ecl.Auth;
using Ecl.Configuration.Settings;
using Ecl.Core;
using Ecl.Core.Localization;
using Ecl.Java;
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
    private bool _initialized;
    private bool _suppressAccountSelection;
    private bool _suppressJavaSelection;
    private bool _suppressJavaCheck;

    public ObservableCollection<InstanceItemViewModel> Instances { get; } = [];

    public ObservableCollection<AccountItemViewModel> Accounts { get; } = [];

    public ObservableCollection<LaunchStepItemViewModel> LaunchSteps { get; } = [];

    public ObservableCollection<JavaOptionViewModel> JavaOptions { get; } = [];

    public ObservableCollection<LoaderChoiceViewModel> LoaderChoices { get; } = [];

    public ObservableCollection<VersionChoiceViewModel> LoaderVersions { get; } = [];

    public ObservableCollection<VersionChoiceViewModel> FabricApiVersions { get; } = [];

    public ObservableCollection<VersionChoiceViewModel> OptiFineVersions { get; } = [];

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
    private bool _isDownloadingJava;

    [ObservableProperty]
    private bool _javaDownloadIndeterminate;

    [ObservableProperty]
    private double _javaDownloadProgressValue;

    [ObservableProperty]
    private string _javaDownloadStatus = string.Empty;

    public bool HasSelection => SelectedInstance is not null;

    public bool IsEmpty => Instances.Count == 0;

    public string SelectedInstanceName => SelectedInstance?.Name ?? string.Empty;

    public string SelectedInstanceMeta => SelectedInstance is null
        ? string.Empty
        : $"{SelectedInstance.Model.VersionId} · {SelectedInstance.Model.Loader}";

    public string SelectedInstancePath => SelectedInstance is null
        ? string.Empty
        : _paths.GetInstanceDirectory(SelectedInstance.Model.Id);

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
        ILoaderCatalog loaderCatalog)
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

        LoaderChoices.Add(new LoaderChoiceViewModel(LoaderKinds.Vanilla, _localizer.Get("instances.loader.vanilla")));
        LoaderChoices.Add(new LoaderChoiceViewModel(LoaderKinds.Fabric, _localizer.Get("instances.loader.fabric")));
        LoaderChoices.Add(new LoaderChoiceViewModel(LoaderKinds.Cleanroom, _localizer.Get("instances.loader.cleanroom")));
        _selectedNewLoader = LoaderChoices[0];
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
                SelectedJavaOption = JavaOptions.FirstOrDefault(option =>
                    option.Runtime is not null &&
                    string.Equals(option.Runtime.ExecutablePath, configured, StringComparison.OrdinalIgnoreCase))
                    ?? JavaOptions[0];
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
        if (!versionResult.IsSuccess)
        {
            JavaDownloadStatus = _localizer.Get("instances.java.installFirst");
            return;
        }

        var requirement = JavaRequirements.Resolve(versionResult.Value!, instance.GameVersion);
        IsBusy = true;
        IsDownloadingJava = true;
        JavaDownloadIndeterminate = true;
        JavaDownloadProgressValue = 0;
        JavaDownloadStatus = _localizer.Get("instances.java.downloading");

        var progress = new Progress<JavaInstallProgress>(update =>
        {
            JavaDownloadIndeterminate = update.IsIndeterminate;
            JavaDownloadProgressValue = update.Percent;
            JavaDownloadStatus = update.TotalFiles > 0
                ? $"{update.Message} ({update.CompletedFiles}/{update.TotalFiles})"
                : update.Message;
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

        if (loader == LoaderKinds.Cleanroom)
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

        var progress = new Progress<InstallProgress>(update =>
        {
            InstallIndeterminate = update.IsIndeterminate;
            InstallProgressValue = update.Percent;
            InstallStatus = update.TotalFiles > 0
                ? $"{update.Message} ({update.CompletedFiles}/{update.TotalFiles})"
                : update.Message;
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
        OptiFineEnabled = string.Equals(value?.Id, LoaderKinds.Cleanroom, StringComparison.OrdinalIgnoreCase);

        if (!_initialized)
        {
            ClearComponentChoices();
            return;
        }

        _ = RefreshComponentsAsync();
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
            SelectedJavaOption = JavaOptions.FirstOrDefault(option =>
                option.Runtime is not null &&
                string.Equals(option.Runtime.ExecutablePath, configured, StringComparison.OrdinalIgnoreCase))
                ?? JavaOptions.FirstOrDefault();
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
