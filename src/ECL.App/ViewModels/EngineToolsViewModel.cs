// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.App.Services;
using Ecl.Minecraft.Engine.Identity;
using Ecl.Minecraft.Engine.Inventory;
using Ecl.Minecraft.Engine.Snapshots;
using Ecl.Minecraft.Instances;

namespace Ecl.App.ViewModels;

/// <summary>
/// 实例引擎原生页：共享 Inventory 与用户快照。所有危险操作都先展示目标并要求确认；
/// 功能不可用或未扫描时显示明确状态，不伪造成功。
/// 安全扫描已迁入独立安全中心（B11），这里不再保留第二套入口。
/// </summary>
public sealed partial class EngineToolsViewModel : ObservableObject
{
    private readonly IInstanceService _instances;
    private readonly IInstanceInventoryService _inventory;
    private readonly IInstanceSnapshotService _snapshots;
    private readonly InstanceIdentityService _identity;
    private readonly IDialogService _dialogs;
    private bool _activated;

    public EngineToolsViewModel(
        IInstanceService instances,
        IInstanceInventoryService inventory,
        IInstanceSnapshotService snapshots,
        InstanceIdentityService identity,
        IDialogService dialogs)
    {
        _instances = instances;
        _inventory = inventory;
        _snapshots = snapshots;
        _identity = identity;
        _dialogs = dialogs;
        Scopes = [SnapshotScope.Full, SnapshotScope.Environment];
        _selectedScope = SnapshotScope.Full;
    }

    public ObservableCollection<EngineInstanceChoice> Instances { get; } = [];

    public ObservableCollection<EngineResourceRow> Resources { get; } = [];

    public ObservableCollection<EngineSnapshotRow> Snapshots { get; } = [];

    public IReadOnlyList<SnapshotScope> Scopes { get; }

    [ObservableProperty]
    private SnapshotScope _selectedScope;

    [ObservableProperty]
    private EngineInstanceChoice? _selectedInstance;

    [ObservableProperty]
    private EngineResourceRow? _selectedResource;

    [ObservableProperty]
    private EngineSnapshotRow? _selectedSnapshot;

    [ObservableProperty]
    private string _status = "选择实例后扫描共享 Inventory 或创建快照。";

    [ObservableProperty]
    private string _identityStatus = "尚未校验实例身份。";

    [ObservableProperty]
    private string _inventorySummary = "尚未扫描资源清单。";

    [ObservableProperty]
    private bool _isBusy;

    public bool HasSelection => SelectedInstance is not null;

    public async Task ActivateAsync()
    {
        if (_activated)
        {
            return;
        }

        _activated = true;
        await ReloadInstancesAsync().ConfigureAwait(true);
    }

    partial void OnSelectedInstanceChanged(EngineInstanceChoice? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        _ = RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (SelectedInstance is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var instanceId = SelectedInstance.Id;
            var cached = await _inventory.GetCachedAsync(instanceId).ConfigureAwait(true);
            ApplyInventory(cached.IsSuccess ? cached.Value : null);

            var snapshots = await _snapshots.ListAsync(instanceId).ConfigureAwait(true);
            Snapshots.Clear();
            if (snapshots.IsSuccess)
            {
                foreach (var snapshot in snapshots.Value!)
                {
                    Snapshots.Add(new EngineSnapshotRow(snapshot));
                }
            }

            var identity = await _identity.VerifyAsync(instanceId).ConfigureAwait(true);
            IdentityStatus = identity.IsSuccess
                ? $"{identity.Value!.Status} · {identity.Value.Message}"
                : identity.Error!.Message;

            Status = cached.IsSuccess && cached.Value is not null
                ? $"已读取 {cached.Value.ScannedAt:yyyy-MM-dd HH:mm} 的 Inventory 缓存。"
                : "尚无 Inventory 缓存，点击“扫描资源”开始。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ScanInventoryAsync()
    {
        if (SelectedInstance is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            Status = "正在扫描实例资源（快速文件 → 资源识别 → 深度分析）…";
            var result = await _inventory.ScanAsync(SelectedInstance.Id).ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                Status = result.Error!.Message;
                return;
            }

            ApplyInventory(result.Value);
            Status = $"Inventory 扫描完成：{result.Value!.Resources.Count} 项资源，{result.Value.Totals.Files} 个文件。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CreateSnapshotAsync()
    {
        if (SelectedInstance is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _snapshots.CreateAsync(
                SelectedInstance.Id,
                new SnapshotCreateOptions(SelectedScope)).ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                Status = result.Error!.Message;
                return;
            }

            Status = result.Value!.Status == SnapshotStatus.Complete
                ? $"已创建快照 {result.Value.SnapshotId}（{result.Value.FileCount} 文件）。"
                : $"快照 {result.Value.SnapshotId} 不完整，不会被用于恢复。";
            await RefreshAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RestoreSnapshotAsync()
    {
        if (SelectedInstance is null || SelectedSnapshot is null)
        {
            Status = "请选择要恢复的快照。";
            return;
        }

        var snapshot = SelectedSnapshot;
        if (!_dialogs.Confirm(
                "恢复实例快照",
                $"将用快照 {snapshot.Id} 覆盖当前实例范围内容，并先创建恢复前快照。是否继续？",
                $"engine.restore.{snapshot.Id}"))
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _snapshots.RestoreAsync(SelectedInstance.Id, snapshot.Id).ConfigureAwait(true);
            Status = result.IsSuccess
                ? $"已恢复快照 {snapshot.Id}（写入 {result.Value!.Written}，删除 {result.Value.Deleted}）。"
                : result.Error!.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DeleteSnapshotAsync()
    {
        if (SelectedInstance is null || SelectedSnapshot is null)
        {
            return;
        }

        var snapshot = SelectedSnapshot;
        if (!_dialogs.Confirm(
                "删除快照",
                $"将删除快照 {snapshot.Id} 的清单；共享内容对象会在显式清理时才回收。是否继续？",
                $"engine.delete-snapshot.{snapshot.Id}"))
        {
            return;
        }

        var result = await _snapshots.DeleteAsync(SelectedInstance.Id, snapshot.Id).ConfigureAwait(true);
        Status = result.IsSuccess ? $"已删除快照 {snapshot.Id}。" : result.Error!.Message;
        await RefreshAsync().ConfigureAwait(true);
    }

    private async Task ReloadInstancesAsync()
    {
        var instances = await _instances.GetInstancesAsync().ConfigureAwait(true);
        Instances.Clear();
        foreach (var instance in instances)
        {
            Instances.Add(new EngineInstanceChoice(
                instance.Id,
                $"{instance.Name} · {instance.GameVersion} · {instance.Loader}"));
        }

        SelectedInstance = Instances.FirstOrDefault();
        if (SelectedInstance is null)
        {
            Status = "尚未创建实例。";
        }
    }

    private void ApplyInventory(InstanceInventory? inventory)
    {
        Resources.Clear();
        if (inventory is null)
        {
            InventorySummary = "尚未扫描资源清单。";
            return;
        }

        foreach (var resource in inventory.Resources)
        {
            Resources.Add(new EngineResourceRow(resource));
        }

        InventorySummary =
            $"实例 {inventory.MinecraftVersion} / {inventory.Loader} · 文件 {inventory.Totals.Files} · 大小 {FormatBytes(inventory.Totals.TotalBytes)} · "
            + $"模组 {inventory.Totals.Mods}（启用 {inventory.Totals.EnabledMods}） · 资源包 {inventory.Totals.ResourcePacks} · 光影 {inventory.Totals.ShaderPacks} · 世界 {inventory.Totals.Worlds} · "
            + $"扫描于 {inventory.ScannedAt:yyyy-MM-dd HH:mm}";
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024d:F1} KiB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024d / 1024:F1} MiB",
        _ => $"{bytes / 1024d / 1024 / 1024:F2} GiB",
    };
}

public sealed record EngineInstanceChoice(string Id, string DisplayName);

public sealed record EngineResourceRow(InventoryResource Resource)
{
    public string Kind => Resource.Kind.ToString();

    public string RelativePath => Resource.RelativePath;

    public string Name => Resource.Name ?? Resource.RelativePath;

    public string EnablementText => Resource.Enablement.ToString();

    public string CompatibilityText =>
        $"MC {Resource.MinecraftCompatibility} · Loader {Resource.LoaderCompatibility}";

    public string DependencyText
    {
        get
        {
            var missing = Resource.Dependencies.Count(item => item.State == DependencyState.Missing);
            return missing == 0 ? "依赖未发现缺失" : $"缺少 {missing} 项必需依赖";
        }
    }

    public bool HasIcon => !string.IsNullOrWhiteSpace(Resource.IconCachePath);
}

public sealed record EngineSnapshotRow(SnapshotManifest Manifest)
{
    public string Id => Manifest.SnapshotId;

    public string ScopeText => Manifest.Scope.ToString();

    public string StatusText => Manifest.Status.ToString();

    public string Summary =>
        $"{Manifest.CreatedAt:yyyy-MM-dd HH:mm} · {Manifest.Scope} · {Manifest.FileCount} 文件 · {Manifest.Status}";
}
