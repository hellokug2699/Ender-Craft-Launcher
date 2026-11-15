// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.App.Services;
using Ecl.App.ViewModels;
using Ecl.Core.Localization;
using Ecl.Minecraft.World;

namespace Ecl.App.Dialogs.ViewModels;

/// <summary>
/// 数据包「安装到其他位置」的目标选择（`download-resource-template.md` §8.3）：
/// 先确认实例，再从该实例的世界清单选择具体世界；不提供任意目录作为数据包目标。
/// 世界条目来自 <see cref="IDatapackService.ListWorldsAsync"/>，界面不拼接世界路径。
/// </summary>
public sealed partial class DatapackTargetDialogViewModel : ObservableObject, IDialogSessionAware
{
    private readonly IDatapackService _datapacks;
    private readonly ILocalizer _localizer;
    private IDialogSession? _session;
    private int _loadGeneration;

    public DatapackTargetDialogViewModel(
        IReadOnlyList<ContentTargetInstanceRow> instances,
        IDatapackService datapacks,
        ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(datapacks);
        ArgumentNullException.ThrowIfNull(localizer);

        Instances = new ObservableCollection<ContentTargetInstanceRow>(instances);
        _datapacks = datapacks;
        _localizer = localizer;
        _selectedInstance = Instances.FirstOrDefault();
        if (_selectedInstance is not null)
        {
            _ = LoadWorldsAsync();
        }
    }

    public ObservableCollection<ContentTargetInstanceRow> Instances { get; }

    public ObservableCollection<DatapackWorldChoice> Worlds { get; } = [];

    public bool HasInstances => Instances.Count > 0;

    public bool HasWorlds => Worlds.Count > 0;

    public bool ShowNoWorlds => HasInstances && !IsLoadingWorlds && !HasWorlds;

    public string InstanceSectionText => _localizer.Get("downloads.discovery.other.instances");

    public string WorldSectionText => _localizer.Get("downloads.discovery.other.worlds");

    public string EmptyInstancesText => _localizer.Get("downloads.discovery.other.none");

    public string EmptyWorldsText => _localizer.Get("downloads.discovery.other.worldNone");

    public string WorldsLoadingText => _localizer.Get("downloads.discovery.other.worldsLoading");

    public string ConfirmText => _localizer.Get("downloads.discovery.other.worldConfirm");

    public string CancelText => _localizer.Get("dialog.cancel");

    [ObservableProperty]
    private ContentTargetInstanceRow? _selectedInstance;

    [ObservableProperty]
    private DatapackWorldChoice? _selectedWorld;

    [ObservableProperty]
    private bool _isLoadingWorlds;

    [ObservableProperty]
    private string _worldErrorText = string.Empty;

    public bool CanConfirm => SelectedInstance is not null && SelectedWorld is not null && !IsLoadingWorlds;

    void IDialogSessionAware.Attach(IDialogSession session) => _session = session;

    private async Task LoadWorldsAsync()
    {
        var generation = ++_loadGeneration;
        Worlds.Clear();
        SelectedWorld = null;
        WorldErrorText = string.Empty;
        NotifyState();

        if (SelectedInstance is not { } instance)
        {
            return;
        }

        IsLoadingWorlds = true;
        NotifyState();
        try
        {
            var result = await _datapacks.ListWorldsAsync(instance.Id).ConfigureAwait(true);
            if (generation != _loadGeneration)
            {
                return;
            }

            if (!result.IsSuccess)
            {
                WorldErrorText = _localizer.Get(
                    "downloads.discovery.target.worldsFailed",
                    result.Error?.Message ?? string.Empty);
                return;
            }

            var scan = result.Value!;
            foreach (var world in scan.Worlds)
            {
                Worlds.Add(new DatapackWorldChoice(
                    world.Name,
                    world.FolderName,
                    _localizer.Get("downloads.discovery.target.worldDetail", world.FolderName),
                    new DatapackTarget(instance.Id, world.FolderName)));
            }

            if (!scan.IsAccessible && WorldErrorText.Length == 0)
            {
                WorldErrorText = _localizer.Get("downloads.discovery.other.worldFailed");
            }
        }
        catch (Exception exception)
        {
            if (generation == _loadGeneration)
            {
                WorldErrorText = _localizer.Get(
                    "downloads.discovery.target.worldsFailed",
                    exception.Message);
            }
        }
        finally
        {
            if (generation == _loadGeneration)
            {
                IsLoadingWorlds = false;
                NotifyState();
            }
        }
    }

    private void NotifyState()
    {
        OnPropertyChanged(nameof(HasInstances));
        OnPropertyChanged(nameof(HasWorlds));
        OnPropertyChanged(nameof(ShowNoWorlds));
        OnPropertyChanged(nameof(CanConfirm));
    }

    partial void OnSelectedInstanceChanged(ContentTargetInstanceRow? value) => _ = LoadWorldsAsync();

    partial void OnSelectedWorldChanged(DatapackWorldChoice? value) => NotifyState();

    partial void OnIsLoadingWorldsChanged(bool value) => NotifyState();

    [RelayCommand]
    private void Confirm()
    {
        if (SelectedInstance is not { } instance || SelectedWorld is not { } world)
        {
            return;
        }

        _session?.TryClose(
            DialogOutcome.Primary,
            new DatapackTargetChoice(instance.Id, instance.Name, world.Target, world.Name));
    }

    [RelayCommand]
    private void Cancel() => _session?.TryClose(DialogOutcome.Cancel);
}

/// <summary>数据包目标选择结果：稳定实例身份 + 世界目标，两者都属于已登记实例。</summary>
public sealed record DatapackTargetChoice(
    string InstanceId,
    string InstanceName,
    DatapackTarget Target,
    string WorldName);
