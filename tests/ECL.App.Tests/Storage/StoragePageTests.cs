// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.IO;
using Ecl.App.Dialogs;
using Ecl.App.Dialogs.ViewModels;
using Ecl.App.Services;
using Ecl.App.ViewModels;
using Ecl.App.ViewModels.Storage;
using Ecl.Core.Diagnostics;
using Ecl.Core.Localization;
using Ecl.Minecraft.Storage;
using Ecl.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ecl.App.Tests.Storage;

/// <summary>
/// 存储管理页（storage-management.md）：固定 32 分类/4 分组、磁盘筛选、实例定位与
/// 白名单勾选；领域扫描与删除由 IStorageInventoryService 负责，页面只做呈现与确认。
/// </summary>
public sealed class StoragePageTests
{
    private static readonly ILocalizer Localizer = new JsonLocalizer(CultureInfo.GetCultureInfo("zh-CN"));

    [Fact]
    public async Task ScanBuildsFourGroupsAndAllFixedCategories()
    {
        var entries = new[]
        {
            Entry("instance.logs", StorageGroup.MinecraftInstance, "old.log", 1024, canClean: true),
            Entry("instance.mods", StorageGroup.MinecraftInstance, "a.jar", 2048, canClean: false),
        };
        var inventory = Inventory(entries);
        var vm = CreateViewModel(inventory);

        await vm.ScanAsync(force: true);

        Assert.True(vm.HasScanned);
        Assert.Equal(4, vm.Groups.Count);
        Assert.Equal(32, vm.Categories.Count);
        Assert.Equal(32, vm.Groups.Sum(group => group.Categories.Count));
        Assert.Contains(vm.Groups, group => group.Title == "Minecraft 实例内容");
        Assert.Contains(vm.Categories, category => category.Title == "下载缓存");
    }

    [Fact]
    public void CategorySelectionOnlyCountsCleanableRowsAndUpdatesDeleteText()
    {
        var category = Category("instance.logs", StorageGroup.MinecraftInstance, 1024, 2048);
        var clean = category.Rows.Single(row => row.FileName == "cleanable.log");
        var protectedRow = category.Rows.Single(row => row.FileName == "protected.log");

        Assert.True(clean.IsSelectable);
        Assert.False(protectedRow.IsSelectable);
        Assert.False(category.CanDelete);

        clean.IsSelected = true;

        Assert.True(category.CanDelete);
        Assert.Equal(1, category.SelectedCount);
        Assert.Equal(1024, category.SelectedBytes);
        Assert.Contains("1 删除", category.DeleteText);
    }

    [Fact]
    public async Task UncheckingAllDisksClearsRowsButKeepsEveryCategory()
    {
        var inventory = Inventory([Entry("instance.logs", StorageGroup.MinecraftInstance, "old.log", 1024, true)]);
        var vm = CreateViewModel(inventory);
        await vm.ScanAsync(force: true);
        var category = vm.Categories.Single(item => item.Id == "instance.logs");
        Assert.Single(category.Rows);

        foreach (var disk in vm.Disks)
        {
            disk.IsSelected = false;
        }

        Assert.Empty(category.Rows);
        Assert.True(category.IsEmpty);
        Assert.Equal(32, vm.Categories.Count);
    }

    [Fact]
    public async Task FocusInstanceFiltersRowsWithoutHidingCategories()
    {
        var inventory = Inventory(
        [
            Entry("instance.logs", StorageGroup.MinecraftInstance, "a.log", 1, true, instanceId: "a", instanceName: "A"),
            Entry("instance.logs", StorageGroup.MinecraftInstance, "b.log", 1, true, instanceId: "b", instanceName: "B"),
        ]);
        var vm = CreateViewModel(inventory);
        await vm.ScanAsync(force: true);

        vm.FocusInstance("a");

        var category = vm.Categories.Single(item => item.Id == "instance.logs");
        Assert.True(vm.HasInstanceFocus);
        Assert.Single(category.Rows);
        Assert.Equal("A", category.Rows[0].OwnerText);
        Assert.Equal(32, vm.Categories.Count);

        vm.ClearInstanceFocus();
        Assert.False(vm.HasInstanceFocus);
        Assert.Equal(2, category.Rows.Count);
    }

    [Fact]
    public async Task DeleteDialogSharesSelectionAndShowsPerItemResult()
    {
        var row = new StorageFileRowViewModel(
            Entry("ecl.downloads", StorageGroup.EclOwn, "old.zip", 100, true),
            Localizer);
        var dialog = new StorageDeleteConfirmViewModel(
            [row],
            Localizer,
            items =>
            {
                foreach (var item in items)
                {
                    item.BeginProgress();
                    item.CompleteProgress(true, null);
                }

                return Task.CompletedTask;
            });
        row.IsSelected = true;

        await dialog.ConfirmCommand.ExecuteAsync(null);

        Assert.True(dialog.DidExecute);
        Assert.True(dialog.IsCompleted);
        Assert.Equal("success", row.ProgressKind);
        Assert.Contains("成功 1 项", dialog.SummaryText);
    }

    [Fact]
    public void LowSpaceDiskIsRedAndUnknownDiskIsNotNormalized()
    {
        var low = new StorageDiskViewModel(
            Disk(total: 100, available: 5),
            Localizer,
            () => { });
        var normal = new StorageDiskViewModel(
            Disk(total: 100, available: 50),
            Localizer,
            () => { });
        var unknown = new StorageDiskViewModel(
            Disk(total: null, available: null, StorageDiskState.Unavailable),
            Localizer,
            () => { });

        Assert.Equal("low", low.RingState);
        Assert.Equal("normal", normal.RingState);
        Assert.Equal("unknown", unknown.RingState);
        Assert.Empty(unknown.Segments);
    }

    private static StorageViewModel CreateViewModel(StorageInventory inventory) =>
        new(
            new FakeInventoryService(inventory),
            Localizer,
            new OperationService(),
            new UnavailableDialogEngine(),
            new UnavailableDialogService(),
            NullLogger<StorageViewModel>.Instance);

    private static StorageInventory Inventory(IReadOnlyList<StorageFileEntry> entries)
    {
        var categories = StorageCategorySpec.All
            .Select(spec =>
            {
                var items = entries.Where(entry => entry.CategoryId == spec.Id).ToArray();
                return new StorageCategory(
                    spec.Id,
                    spec.Group,
                    items,
                    items.Sum(item => item.Size ?? 0),
                    items.Count(item => item.Size is not null),
                    items.Count(item => item.Size is null),
                    false);
            })
            .ToArray();
        return new StorageInventory(
            categories,
            [Disk(100, 50)],
            entries.Sum(entry => entry.Size ?? 0),
            0,
            1,
            true,
            DateTimeOffset.UtcNow,
            []);
    }

    private static StorageDisk Disk(
        long? total,
        long? available,
        StorageDiskState state = StorageDiskState.Ready) =>
        new(
            "C:\\",
            "C:",
            state,
            total,
            available,
            10,
            new Dictionary<StorageGroup, long> { [StorageGroup.MinecraftInstance] = 10 });

    private static StorageFileEntry Entry(
        string categoryId,
        StorageGroup group,
        string name,
        long? size,
        bool canClean,
        string? instanceId = null,
        string? instanceName = null) =>
        new()
        {
            Id = "file:" + name,
            CategoryId = categoryId,
            Group = group,
            FullPath = Path.Combine(Path.GetTempPath(), name),
            AllowedRoot = Path.GetTempPath(),
            Size = size,
            LastWriteTimeUtc = DateTimeOffset.UtcNow.AddDays(-30),
            InstanceId = instanceId,
            InstanceName = instanceName,
            Owner = instanceName,
            CanClean = canClean,
            ProtectionReason = canClean ? null : "userData",
            Risk = canClean ? StorageRiskLevel.Caution : StorageRiskLevel.None,
        };

    private static StorageCategoryViewModel Category(
        string categoryId,
        StorageGroup group,
        long cleanableBytes,
        long protectedBytes)
    {
        var category = new StorageCategory(
            categoryId,
            group,
            [
                new StorageFileEntry
                {
                    Id = "cleanable",
                    CategoryId = categoryId,
                    Group = group,
                    FullPath = Path.Combine(Path.GetTempPath(), "cleanable.log"),
                    AllowedRoot = Path.GetTempPath(),
                    Size = cleanableBytes,
                    LastWriteTimeUtc = DateTimeOffset.UtcNow.AddDays(-30),
                    CanClean = true,
                    Risk = StorageRiskLevel.Caution,
                },
                new StorageFileEntry
                {
                    Id = "protected",
                    CategoryId = categoryId,
                    Group = group,
                    FullPath = Path.Combine(Path.GetTempPath(), "protected.log"),
                    AllowedRoot = Path.GetTempPath(),
                    Size = protectedBytes,
                    LastWriteTimeUtc = DateTimeOffset.UtcNow.AddDays(-30),
                    CanClean = false,
                    ProtectionReason = "userData",
                },
            ],
            cleanableBytes + protectedBytes,
            2,
            0,
            false);
        return new StorageCategoryViewModel(category, Localizer, _ => { });
    }

    private sealed class FakeInventoryService(StorageInventory inventory) : IStorageInventoryService
    {
        public Task<StorageInventory> ScanAsync(
            StorageInventoryScanOptions? options = null,
            IProgress<StorageScanProgress>? progress = null,
            CancellationToken cancellationToken = default) => Task.FromResult(inventory);

        public Task<StorageDeleteResult> DeleteAsync(
            IReadOnlyList<StorageFileEntry> entries,
            IProgress<StorageDeleteProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new StorageDeleteResult([]));
    }

    private sealed class UnavailableDialogEngine : IDialogEngine
    {
        public bool IsAvailable => false;

        public DialogResult Show(DialogRequest request) => DialogResult.Cancel;

        public Task<DialogResult> ShowAsync(DialogRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(DialogResult.Cancel);

        public bool CloseTop(DialogOutcome outcome) => false;
    }

    private sealed class UnavailableDialogService : IDialogService
    {
        public bool Confirm(string title, string message, string? acknowledgementToken = null) => false;

        public InstanceDeletionChoice ChooseInstanceDeletion(string title, string message, bool canDeleteManagedData) =>
            InstanceDeletionChoice.Cancel;

        public void ShowInfo(string title, string message)
        {
        }

        public void ShowError(string title, string message)
        {
        }

        public string? PickOpenFile(string title, string filter) => null;

        public IReadOnlyList<string> PickOpenFiles(string title, string filter) => [];

        public string? PickSaveFile(string title, string filter, string? defaultFileName = null) => null;

        public string? PickFolder(string title) => null;

        public bool OpenUrl(string url) => false;
    }
}
