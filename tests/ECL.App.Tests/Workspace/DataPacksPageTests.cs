// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.IO;
using Ecl.App.Services;
using Ecl.App.ViewModels;
using Ecl.App.Workspace.ViewModels;
using Ecl.Core;
using Ecl.Core.Localization;
using Ecl.Minecraft.Mods;
using Ecl.Minecraft.World;
using Ecl.World.Worlds;
using Xunit;

namespace Ecl.App.Tests.Workspace;

/// <summary>
/// 实例工作区数据包页（B2 收尾，resource-library-template.md §5/§5.1）：
/// 世界目标门槛、双表与独立计数、待应用顺序、一次提交到选定世界、删除确认、
/// 切换世界丢弃待应用更改，以及状态/版本范围只来自真实数据。
/// </summary>
public sealed class DataPacksPageTests
{
    private static ILocalizer Localizer() => new JsonLocalizer(CultureInfo.GetCultureInfo("zh-CN"));

    [Fact]
    public async Task NoWorldIsSelectedOrWrittenUntilTheUserPicksOne()
    {
        var datapacks = new FakeDatapackService();
        datapacks.Worlds.Add(World("Alpha World", "AlphaWorld"));
        datapacks.Worlds.Add(World("Beta World", "BetaWorld"));
        var page = Create(datapacks);

        await page.InitializeAsync();

        Assert.Equal(2, page.Worlds.Count);
        Assert.Null(page.SelectedWorld);
        Assert.Empty(page.EnabledPacks);
        Assert.Empty(page.DisabledPacks);
        Assert.False(page.ApplyCommand.CanExecute(null));
        Assert.Contains("请先选择目标世界", page.WorldHint);
        Assert.Contains("请先选择目标世界", page.EmptyEnabledMessage);
        Assert.Equal(0, datapacks.ListCalls);
    }

    [Fact]
    public async Task SelectingAWorldSplitsEnabledAndDisabledTablesWithIndependentCounts()
    {
        var datapacks = new FakeDatapackService();
        datapacks.Worlds.Add(World("Alpha World", "AlphaWorld"));
        datapacks.Lists["AlphaWorld"] = List(
            Pack("one.zip", enabled: true),
            Pack("two.zip", enabled: true),
            Pack("three.zip", enabled: false));
        var page = Create(datapacks);
        await page.InitializeAsync();

        page.SelectedWorld = page.Worlds[0];
        await DrainAsync(page);

        Assert.Equal(["one.zip", "two.zip"], RowNames(page.EnabledPacks));
        Assert.Equal(["three.zip"], RowNames(page.DisabledPacks));
        Assert.Equal("共 2 个", page.EnabledCountText);
        Assert.Equal("共 1 个", page.DisabledCountText);
        Assert.Equal("AlphaWorld", datapacks.LastTarget?.FolderName);
        Assert.Contains("Alpha World", page.WorldHint);
        Assert.False(page.HasPendingChanges);
    }

    [Fact]
    public async Task DragAndToggleStayPendingUntilApplySubmitsTheOrderedListToTheWorld()
    {
        var datapacks = new FakeDatapackService();
        datapacks.Worlds.Add(World("Alpha World", "AlphaWorld"));
        datapacks.Lists["AlphaWorld"] = List(
            Pack("alpha.zip", enabled: true),
            Pack("beta.zip", enabled: false),
            Pack("gamma.zip", enabled: false));
        var page = Create(datapacks);
        await page.InitializeAsync();
        page.SelectedWorld = page.Worlds[0];
        await DrainAsync(page);

        page.MovePack(page.DisabledPacks[0], toEnabled: true, page.EnabledPacks.Count);
        page.MoveWithin(page.EnabledPacks[0], enabledTable: true, targetIndex: 1);

        Assert.Equal(["beta.zip", "alpha.zip"], RowNames(page.EnabledPacks));
        Assert.True(page.HasPendingChanges);
        Assert.True(page.ApplyCommand.CanExecute(null));
        Assert.Empty(datapacks.AppliedOrders);

        await page.ApplyCommand.ExecuteAsync(null);

        var applied = Assert.Single(datapacks.AppliedOrders);
        Assert.Equal("AlphaWorld", applied.Folder);
        Assert.Equal(["beta.zip", "alpha.zip"], applied.Names);
        Assert.False(page.HasPendingChanges);
        Assert.Equal(["beta.zip", "alpha.zip"], RowNames(page.EnabledPacks));
        Assert.Equal(["gamma.zip"], RowNames(page.DisabledPacks));
    }

    [Fact]
    public async Task SwitchingWorldRequiresDiscardingPendingChangesAndReloadsTheNewWorld()
    {
        var datapacks = new FakeDatapackService();
        datapacks.Worlds.Add(World("Alpha World", "AlphaWorld"));
        datapacks.Worlds.Add(World("Beta World", "BetaWorld"));
        datapacks.Lists["AlphaWorld"] = List(Pack("alpha.zip", enabled: false));
        datapacks.Lists["BetaWorld"] = List(Pack("beta.zip", enabled: true));
        var dialogs = new FakeDialogService();
        var page = Create(datapacks, dialogs);
        await page.InitializeAsync();

        page.SelectedWorld = page.Worlds[0];
        await DrainAsync(page);
        page.TogglePackCommand.Execute(page.DisabledPacks[0]);
        Assert.True(page.HasPendingChanges);

        // 取消丢弃：保持原世界与待应用更改，不读取另一个世界。
        dialogs.ConfirmResult = false;
        Assert.False(page.TrySelectWorld(page.Worlds[1]));
        await DrainAsync(page);
        Assert.Same(page.Worlds[0], page.SelectedWorld);
        Assert.True(page.HasPendingChanges);
        Assert.Equal(1, datapacks.ListCalls);

        // 确认丢弃后重新加载新世界，旧世界的待应用更改不会带入。
        dialogs.ConfirmResult = true;
        Assert.True(page.TrySelectWorld(page.Worlds[1]));
        await DrainAsync(page);
        Assert.Same(page.Worlds[1], page.SelectedWorld);
        Assert.False(page.HasPendingChanges);
        Assert.Equal(["beta.zip"], RowNames(page.EnabledPacks));
        Assert.Empty(page.DisabledPacks);
    }

    [Fact]
    public async Task DatapackFormatsAreNotMappedThroughTheResourcePackVersionTable()
    {
        var datapacks = new FakeDatapackService();
        datapacks.Worlds.Add(World("Alpha World", "AlphaWorld"));
        datapacks.Lists["AlphaWorld"] = List(
            Pack("declared.zip", enabled: true, supportedFormats: new PackFormatRange(15, 18)),
            Pack("known.zip", enabled: true, packFormat: 15),
            Pack("no-metadata.zip", enabled: true, packFormat: null));
        var page = Create(datapacks);
        await page.InitializeAsync();
        page.SelectedWorld = page.Worlds[0];
        await DrainAsync(page);

        string Range(string fileName) =>
            page.EnabledPacks.Single(pack => pack.FileName == fileName).VersionRangeText;

        // 资源包的 15 会映射到 1.20 – 1.20.1；数据包格式编号完全不同，不得复用该表。
        Assert.Equal("格式 15–18", Range("declared.zip"));
        Assert.Equal("格式 15", Range("known.zip"));
        Assert.Equal("未知", Range("no-metadata.zip"));
    }

    [Fact]
    public async Task DeleteUsesTheWorldTargetAndReportsTheDomainResult()
    {
        var datapacks = new FakeDatapackService();
        datapacks.Worlds.Add(World("Alpha World", "AlphaWorld"));
        datapacks.Lists["AlphaWorld"] = List(Pack("alpha.zip", enabled: false));
        var dialogs = new FakeDialogService();
        var page = Create(datapacks, dialogs);
        await page.InitializeAsync();
        page.SelectedWorld = page.Worlds[0];
        await DrainAsync(page);

        dialogs.ConfirmResult = false;
        await page.DeleteDatapackCommand.ExecuteAsync(page.DisabledPacks[0]);
        Assert.Empty(datapacks.DeletedFiles);

        dialogs.ConfirmResult = true;
        await page.DeleteDatapackCommand.ExecuteAsync(page.DisabledPacks[0]);

        var deleted = Assert.Single(datapacks.DeletedFiles);
        Assert.Equal("AlphaWorld", deleted.Folder);
        Assert.Equal("alpha.zip", deleted.FileName);
        Assert.Empty(page.DisabledPacks);
        Assert.Contains("已删除", page.StatusMessage);
    }

    [Fact]
    public async Task ApplyFailureKeepsPendingChanges()
    {
        var datapacks = new FakeDatapackService { FailApply = true };
        datapacks.Worlds.Add(World("Alpha World", "AlphaWorld"));
        datapacks.Lists["AlphaWorld"] = List(
            Pack("alpha.zip", enabled: true),
            Pack("beta.zip", enabled: false));
        var page = Create(datapacks);
        await page.InitializeAsync();
        page.SelectedWorld = page.Worlds[0];
        await DrainAsync(page);

        page.TogglePackCommand.Execute(page.DisabledPacks[0]);
        await page.ApplyCommand.ExecuteAsync(null);

        Assert.True(page.HasPendingChanges);
        Assert.Contains("写入失败", page.StatusMessage);
        Assert.Equal(["alpha.zip", "beta.zip"], RowNames(page.EnabledPacks));
    }

    [Fact]
    public async Task RefreshWithPendingChangesRequiresConfirmation()
    {
        var datapacks = new FakeDatapackService();
        datapacks.Worlds.Add(World("Alpha World", "AlphaWorld"));
        datapacks.Lists["AlphaWorld"] = List(
            Pack("alpha.zip", enabled: true),
            Pack("beta.zip", enabled: false));
        var dialogs = new FakeDialogService();
        var page = Create(datapacks, dialogs);
        await page.InitializeAsync();
        page.SelectedWorld = page.Worlds[0];
        await DrainAsync(page);
        page.TogglePackCommand.Execute(page.DisabledPacks[0]);
        Assert.True(page.HasPendingChanges);

        dialogs.ConfirmResult = false;
        await page.RefreshCommand.ExecuteAsync(null);
        Assert.True(page.HasPendingChanges);
        Assert.Equal(1, datapacks.ListCalls);

        dialogs.ConfirmResult = true;
        await page.RefreshCommand.ExecuteAsync(null);
        Assert.False(page.HasPendingChanges);
        Assert.Equal(2, datapacks.ListCalls);
        Assert.Equal(["alpha.zip"], RowNames(page.EnabledPacks));
        Assert.Equal(["beta.zip"], RowNames(page.DisabledPacks));
    }

    [Fact]
    public async Task MissingUnmanagedAndDuplicateEntriesAreSurfacedAsWarnings()
    {
        var datapacks = new FakeDatapackService();
        datapacks.Worlds.Add(World("Alpha World", "AlphaWorld"));
        datapacks.Lists["AlphaWorld"] = new DatapackList(
            [Pack("alpha.zip", enabled: true)],
            ["file/gone.zip"],
            ["fabric"],
            ["file/alpha.zip"],
            DatapackSelectionStatus.NotInitialized,
            null);
        var page = Create(datapacks);
        await page.InitializeAsync();
        page.SelectedWorld = page.Worlds[0];
        await DrainAsync(page);

        Assert.Contains("缺少文件", page.WarningText);
        Assert.Contains("本页不管理", page.WarningText);
        Assert.Contains("重复条目", page.WarningText);
        Assert.Contains("vanilla", page.WarningText);
    }

    [Fact]
    public async Task InstanceWithoutReadableWorldsShowsAnAccurateEmptyState()
    {
        var datapacks = new FakeDatapackService();
        var page = Create(datapacks);
        await page.InitializeAsync();

        Assert.Empty(page.Worlds);
        Assert.True(page.ShowNoWorlds);
        Assert.Contains("没有可读取的世界", page.NoWorldsText);
    }

    [Fact]
    public async Task WorldListFailureIsSurfacedInsteadOfPretendingThereAreNoWorlds()
    {
        var datapacks = new FakeDatapackService { FailWorlds = true };
        var page = Create(datapacks);
        await page.InitializeAsync();

        Assert.Empty(page.Worlds);
        Assert.False(page.ShowNoWorlds);
        Assert.Contains("读取世界清单失败", page.StatusMessage);
    }

    [Fact]
    public async Task WorldScanWarningsAreShownWithoutDroppingReadableWorlds()
    {
        var datapacks = new FakeDatapackService { WorldScanWarnings = ["WorldCorrupt: level.dat 无法读取"] };
        datapacks.Worlds.Add(World("Alpha World", "AlphaWorld"));
        datapacks.Lists["AlphaWorld"] = List(Pack("alpha.zip", enabled: true));
        var page = Create(datapacks);
        await page.InitializeAsync();

        Assert.Single(page.Worlds);
        Assert.Contains("level.dat 无法读取", page.WorldScanWarning);

        page.SelectedWorld = page.Worlds[0];
        await DrainAsync(page);
        Assert.Contains("level.dat 无法读取", page.WarningText);
    }

    [Fact]
    public async Task AddUsesTheFilePickerAndReloadsTheSelectedWorldOnly()
    {
        var datapacks = new FakeDatapackService();
        datapacks.Worlds.Add(World("Alpha World", "AlphaWorld"));
        datapacks.Lists["AlphaWorld"] = List(Pack("alpha.zip", enabled: true));
        var dialogs = new FakeDialogService { PickOpenFileResult = @"C:\downloads\new.zip" };
        var page = Create(datapacks, dialogs);
        await page.InitializeAsync();
        page.SelectedWorld = page.Worlds[0];
        await DrainAsync(page);

        await page.AddDatapackCommand.ExecuteAsync(null);

        var imported = Assert.Single(datapacks.ImportedFiles);
        Assert.Equal("AlphaWorld", imported.Folder);
        Assert.Equal(@"C:\downloads\new.zip", imported.FileName);
        Assert.Equal(2, datapacks.ListCalls);
        Assert.Contains("已添加", page.StatusMessage);
    }

    [Fact]
    public async Task DownloadEntryIsOnlyAvailableWhenShellProvidesIt()
    {
        var datapacks = new FakeDatapackService();
        var page = Create(datapacks);

        Assert.False(page.CanOpenBrowser);
        page.DownloadDatapacksCommand.Execute(null);

        string? requested = null;
        page.OpenDatapackBrowser = instanceId => requested = instanceId;
        Assert.True(page.CanOpenBrowser);
        page.DownloadDatapacksCommand.Execute(null);
        Assert.Equal("instance-a", requested);
    }

    [Fact]
    public async Task DisposedPageDoesNotReadWorldsOrLists()
    {
        var datapacks = new FakeDatapackService();
        datapacks.Worlds.Add(World("Alpha World", "AlphaWorld"));
        var page = Create(datapacks);
        page.Dispose();

        await page.InitializeAsync();

        Assert.True(page.IsDisposed);
        Assert.Equal(0, datapacks.WorldCalls);
        Assert.Equal(0, datapacks.ListCalls);
    }

    private static async Task DrainAsync(InstanceDataPacksViewModel page)
    {
        for (var attempt = 0; attempt < 200 && page.IsBusy; attempt++)
        {
            await Task.Delay(10);
        }
    }

    private static string[] RowNames(IEnumerable<DatapackRowViewModel> rows) =>
        rows.Select(row => row.FileName).ToArray();

    private static InstanceDataPacksViewModel Create(
        FakeDatapackService datapacks,
        FakeDialogService? dialogs = null) =>
        new("instance-a", datapacks, dialogs ?? new FakeDialogService(), Localizer());

    private static WorldSave World(string name, string folder) =>
        new(
            name,
            $@"C:\instance\saves\{folder}",
            $@"C:\instance\saves\{folder}\level.dat",
            new WorldLevelInfo { LevelName = name },
            [],
            null);

    private static DatapackList List(params DatapackEntry[] packs) =>
        new(packs, [], [], [], DatapackSelectionStatus.Valid, null);

    private static DatapackEntry Pack(
        string fileName,
        bool enabled,
        int? packFormat = 15,
        PackFormatRange? supportedFormats = null,
        DatapackReadState readState = DatapackReadState.Ok) =>
        new(
            fileName,
            $@"C:\instance\saves\AlphaWorld\datapacks\{fileName}",
            Path.GetFileNameWithoutExtension(fileName),
            null,
            packFormat,
            enabled)
        {
            ReadState = readState,
            SupportedFormats = supportedFormats,
        };

    private sealed class FakeDatapackService : IDatapackService
    {
        public List<WorldSave> Worlds { get; } = [];

        public Dictionary<string, DatapackList> Lists { get; } = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<string> WorldScanWarnings { get; init; } = [];

        public bool FailWorlds { get; init; }

        public bool FailApply { get; init; }

        public int WorldCalls { get; private set; }

        public int ListCalls { get; private set; }

        public DatapackTarget? LastTarget { get; private set; }

        public List<(string Folder, string[] Names)> AppliedOrders { get; } = [];

        public List<(string Folder, string FileName)> DeletedFiles { get; } = [];

        public List<(string Folder, string FileName)> ImportedFiles { get; } = [];

        public Task<Result<WorldScanResult>> ListWorldsAsync(
            string instanceId,
            CancellationToken cancellationToken = default)
        {
            WorldCalls++;
            return Task.FromResult(FailWorlds
                ? Result<WorldScanResult>.Fail("world.scan_failed", "世界目录无法读取。")
                : Result<WorldScanResult>.Ok(new WorldScanResult(Worlds.ToArray(), WorldScanWarnings, true)));
        }

        public Task<Result<DatapackList>> ListAsync(
            string instanceId,
            DatapackTarget target,
            CancellationToken cancellationToken = default)
        {
            ListCalls++;
            LastTarget = target;
            return Task.FromResult(Lists.TryGetValue(target.FolderName, out var list)
                ? Result<DatapackList>.Ok(list)
                : Result<DatapackList>.Fail("datapack.world_missing", "世界不存在。"));
        }

        public Task<Result> ApplyAsync(
            string instanceId,
            DatapackTarget target,
            IReadOnlyList<string> orderedFileNames,
            CancellationToken cancellationToken = default)
        {
            if (FailApply)
            {
                return Task.FromResult(Result.Fail("datapack.level_unreadable", "level.dat 写入失败。"));
            }

            AppliedOrders.Add((target.FolderName, [.. orderedFileNames]));
            var enabled = new HashSet<string>(orderedFileNames, StringComparer.OrdinalIgnoreCase);
            if (Lists.TryGetValue(target.FolderName, out var list))
            {
                var reordered = orderedFileNames
                    .Select(name => list.Packs.Single(pack => pack.FileName == name) with { Enabled = true })
                    .Concat(list.Packs
                        .Where(pack => !enabled.Contains(pack.FileName))
                        .Select(pack => pack with { Enabled = false }))
                    .ToArray();
                Lists[target.FolderName] = list with { Packs = reordered };
            }

            return Task.FromResult(Result.Ok());
        }

        public Task<Result<DatapackEntry>> ImportAsync(
            string instanceId,
            DatapackTarget target,
            string sourcePath,
            bool overwrite = false,
            CancellationToken cancellationToken = default)
        {
            ImportedFiles.Add((target.FolderName, sourcePath));
            return Task.FromResult(Result<DatapackEntry>.Ok(Pack(Path.GetFileName(sourcePath), enabled: false)));
        }

        public Task<Result> DeleteAsync(
            string instanceId,
            DatapackTarget target,
            string fileName,
            CancellationToken cancellationToken = default)
        {
            DeletedFiles.Add((target.FolderName, fileName));
            if (Lists.TryGetValue(target.FolderName, out var list))
            {
                Lists[target.FolderName] = list with
                {
                    Packs = list.Packs.Where(pack => pack.FileName != fileName).ToArray(),
                };
            }

            return Task.FromResult(Result.Ok());
        }
    }

    private sealed class FakeDialogService : IDialogService
    {
        public bool ConfirmResult { get; set; } = true;

        public string? PickOpenFileResult { get; set; }

        public bool Confirm(string title, string message, string? acknowledgementToken = null) => ConfirmResult;

        public InstanceDeletionChoice ChooseInstanceDeletion(string title, string message, bool canDeleteManagedData) =>
            InstanceDeletionChoice.Cancel;

        public void ShowInfo(string title, string message)
        {
        }

        public void ShowError(string title, string message)
        {
        }

        public string? PickOpenFile(string title, string filter) => PickOpenFileResult;

        public IReadOnlyList<string> PickOpenFiles(string title, string filter) => [];

        public string? PickSaveFile(string title, string filter, string? defaultFileName = null) => null;

        public string? PickFolder(string title) => null;

        public bool OpenUrl(string url) => false;
    }
}
