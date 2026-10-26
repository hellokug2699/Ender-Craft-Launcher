// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.App.Services;
using Ecl.Auth;
using Ecl.Configuration.Settings;
using Ecl.Core;
using Ecl.Core.Localization;
using Ecl.Minecraft.Instances;
using Ecl.Minecraft.Launch;

namespace Ecl.App.ViewModels;

public sealed partial class InstancesViewModel : ObservableObject
{
    private readonly IInstanceService _instances;
    private readonly IAccountService _accounts;
    private readonly ILaunchService _launchService;
    private readonly ISettingsService _settings;
    private readonly IDialogService _dialogs;
    private readonly ILocalizer _localizer;
    private readonly IAppPaths _paths;
    private bool _initialized;
    private bool _suppressAccountSelection;

    public ObservableCollection<InstanceItemViewModel> Instances { get; } = [];

    public ObservableCollection<AccountItemViewModel> Accounts { get; } = [];

    public ObservableCollection<LaunchStepItemViewModel> LaunchSteps { get; } = [];

    [ObservableProperty]
    private InstanceItemViewModel? _selectedInstance;

    [ObservableProperty]
    private AccountItemViewModel? _selectedAccount;

    [ObservableProperty]
    private string _newInstanceName = string.Empty;

    [ObservableProperty]
    private string _newInstanceVersion = "1.21.1";

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

    public InstancesViewModel(
        IInstanceService instances,
        IAccountService accounts,
        ILaunchService launchService,
        ISettingsService settings,
        IDialogService dialogs,
        ILocalizer localizer,
        IAppPaths paths)
    {
        _instances = instances;
        _accounts = accounts;
        _launchService = launchService;
        _settings = settings;
        _dialogs = dialogs;
        _localizer = localizer;
        _paths = paths;
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
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await RefreshAccountsAsync().ConfigureAwait(true);
        await RefreshInstancesAsync().ConfigureAwait(true);
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

        IsBusy = true;
        try
        {
            var result = await _instances.CreateAsync(name, version).ConfigureAwait(true);
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

    partial void OnSelectedInstanceChanged(InstanceItemViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedInstanceName));
        OnPropertyChanged(nameof(SelectedInstanceMeta));
        OnPropertyChanged(nameof(SelectedInstancePath));
        OnPropertyChanged(nameof(SelectedInstanceCreated));

        LaunchSteps.Clear();
        LaunchStatus = string.Empty;
        LaunchCommandPreview = string.Empty;
    }

    partial void OnSelectedAccountChanged(AccountItemViewModel? value)
    {
        if (_suppressAccountSelection || value is null || value.IsDefault)
        {
            return;
        }

        _ = SetDefaultAccountAsync(value.Id);
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
