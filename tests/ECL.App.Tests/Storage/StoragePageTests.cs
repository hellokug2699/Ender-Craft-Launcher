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
/// 存储管理页：分类 → 实例（或“全部”）→ 条目，实例行复选框全选该实例在本分类下的内容；
/// 只列出可清理分类，领域扫描与删除由 IStorageInventoryService 负责。
/// </summary>
public sealed class StoragePageTests
{
    private static readonly ILocalizer Localizer = new JsonLocalizer(CultureInfo.GetCultureInfo("zh-CN"));

    [Fact]
    public async Task ScanBuildsThreeGroupsAndOnlyCleanupCategories()
    {
        var entries = new[]
        {
            Entry("instance.logs", StorageGroup.MinecraftInstance, "old.log", 1024, canClean: true, "a", "A"),
            Entry("ecl.downloads", StorageGroup.EclOwn, "old.zip", 2048, canClean: true),
        };
        var inventory = Inventory(entries);
        var vm = CreateViewModel(inventory);

        await vm.ScanAsync(force: true);

        Assert.True(vm.HasScanned);
        Assert.Equal(3, vm.Groups.Count);
        Assert.Equal(17, vm.Categories.Count);
        Assert.Equal(17, vm.Groups.Sum(group => group.Categories.Count));
        Assert.Contains(vm.Groups, group => group.Title == "Minecraft 实例内容");
        Assert.Contains(vm.Groups, group => group.Title == "ECL 自身文件");
        Assert.Contains(vm.Groups, group => group.Title == "扩展数据");
        Assert.Contains(vm.Categories, category => category.Title == "下载缓存");
        Assert.DoesNotContain(vm.Categories, category => category.Id == "instance.mods");
        Assert.DoesNotContain(vm.Categories, category => category.Id == "instance.schematics");
        Assert.DoesNotContain(vm.Categories, category => category.Id == "instance.versionFiles");
    }

    [Fact]
    public void CategoryGroupsByInstanceAndGroupCheckboxSelectsThatInstance()
    {
        var category = Category(
            "instance.logs",
            StorageGroup.MinecraftInstance,
            1024,
            Entry("instance.logs", StorageGroup.MinecraftInstance, "a1.log", 512, true, "a", "A"),
            Entry("instance.logs", StorageGroup.MinecraftInstance, "a2.log", 512, true, "a", "A"),
            Entry("instance.logs", StorageGroup.MinecraftInstance, "b1.log", 256, true, "b", "B"));

        Assert.Equal(2, category.Groups.Count);
        var groupA = category.Groups.Single(group => group.Title == "A");
        var groupB = category.Groups.Single(group => group.Title == "B");
        Assert.Equal(2, groupA.Rows.Count);
        Assert.Single(groupB.Rows);

        groupA.IsSelected = true;

        Assert.All(groupA.Rows, row => Assert.True(row.IsSelected));
        Assert.All(groupB.Rows, row => Assert.False(row.IsSelected));
        Assert.Equal(2, category.SelectedCount);
        Assert.Equal(1024, category.SelectedBytes);
        Assert.Contains("2 删除", category.DeleteText);

        groupA.IsSelected = false;

        Assert.All(groupA.Rows, row => Assert.False(row.IsSelected));
        Assert.False(category.CanDelete);
    }

    [Fact]
    public void GroupCheckboxReflectsPartialSelection()
    {
        var category = Category(
            "instance.logs",
            StorageGroup.MinecraftInstance,
            1024,
            Entry("instance.logs", StorageGroup.MinecraftInstance, "a1.log", 512, true, "a", "A"),
            Entry("instance.logs", StorageGroup.MinecraftInstance, "a2.log", 512, true, "a", "A"));
        var group = Assert.Single(category.Groups);

        Assert.False(group.IsSelected);

        group.Rows[0].IsSelected = true;
        Assert.Null(group.IsSelected);

        group.Rows[1].IsSelected = true;
        Assert.True(group.IsSelected);

        group.Rows[0].IsSelected = false;
        Assert.Null(group.IsSelected);
    }

    [Fact]
    public void UngroupedCategoryUsesAllItemsGroup()
    {
        var category = Category(
            "ecl.downloads",
            StorageGroup.EclOwn,
            100,
            Entry("ecl.downloads", StorageGroup.EclOwn, "old.zip", 100, true));
        var group = Assert.Single(category.Groups);

        Assert.Equal("全部", group.Title);
        Assert.Single(group.Rows);
    }

    [Fact]
    public async Task UncheckingAllDisksClearsGroupsButKeepsEveryCategory()
    {
        var inventory = Inventory([Entry("instance.logs", StorageGroup.MinecraftInstance, "old.log", 1024, true)]);
        var vm = CreateViewModel(inventory);
        await vm.ScanAsync(force: true);
        var category = vm.Categories.Single(item => item.Id == "instance.logs");
        Assert.Single(category.Groups);

        foreach (var disk in vm.Disks)
        {
            disk.IsSelected = false;
        }

        Assert.Empty(category.Groups);
        Assert.True(category.IsEmpty);
        Assert.Equal(17, vm.Categories.Count);
    }

    [Fact]
    public async Task FocusInstanceFiltersGroupsWithoutHidingCategories()
    {
        var inventory = Inventory(
        [
            Entry("instance.logs", StorageGroup.MinecraftInstance, "a.log", 1, true, "a", "A"),
            Entry("instance.logs", StorageGroup.MinecraftInstance, "b.log", 1, true, "b", "B"),
        ]);
        var vm = CreateViewModel(inventory);
        await vm.ScanAsync(force: true);

        vm.FocusInstance("a");

        var category = vm.Categories.Single(item => item.Id == "instance.logs");
        Assert.True(vm.HasInstanceFocus);
        var group = Assert.Single(category.Groups);
        Assert.Equal("A", group.Title);
        Assert.Equal(17, vm.Categories.Count);

        vm.ClearInstanceFocus();
        Assert.False(vm.HasInstanceFocus);
        Assert.Equal(2, category.Groups.Count);
    }

    [Fact]
    public void DirectoryRowsExposeFolderTag()
    {
        var row = new StorageFileRowViewModel(
            Entry("instance.saves", StorageGroup.MinecraftInstance, "world", 4096, true, "a", "A", isDirectory: true),
            Localizer);

        Assert.True(row.HasKindTag);
        Assert.Equal("文件夹", row.KindTag);
        Assert.Equal("world", row.FileName);
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
        string? instanceName = null,
        bool isDirectory = false) =>
        new()
        {
            Id = (isDirectory ? "dir:" : "file:") + name,
            CategoryId = categoryId,
            Group = group,
            FullPath = Path.Combine(Path.GetTempPath(), name),
            AllowedRoot = Path.GetTempPath(),
            IsDirectory = isDirectory,
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
        long totalBytes,
        params StorageFileEntry[] entries)
    {
        var category = new StorageCategory(
            categoryId,
            group,
            entries,
            totalBytes,
            entries.Length,
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
