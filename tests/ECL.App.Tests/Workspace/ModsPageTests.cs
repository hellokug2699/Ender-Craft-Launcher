// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.IO;
using Ecl.App.Services;
using Ecl.App.ViewModels;
using Ecl.Core;
using Ecl.Core.Localization;
using Ecl.Minecraft.Mods;
using Xunit;

namespace Ecl.App.Tests.Workspace;

/// <summary>
/// 模组页（B2 步骤 3b-3）：状态分类、排序、筛选、搜索与批量选择只做真实数据投影，
/// 更新/危险等缺失能力保持不可用，不得假执行。
/// </summary>
public sealed class ModsPageTests
{
    private static ILocalizer Localizer() => new JsonLocalizer(CultureInfo.GetCultureInfo("zh-CN"));

    [Fact]
    public void SevenStatusCategoriesMapFromDomainIssues()
    {
        Assert.Equal(new[] { ModStatusKind.Available }, Kinds(Entry("available.jar", metadata: Metadata("available"))));
        Assert.Equal(new[] { ModStatusKind.Disabled }, Kinds(Entry("off.jar", enabled: false, metadata: Metadata("off"))));
        Assert.Equal(
            new[] { ModStatusKind.Unknown },
            Kinds(Entry("unknown.jar", metadata: null, error: "未找到模组元数据")));
        Assert.Equal(
            new[] { ModStatusKind.MissingDependency },
            Kinds(Entry("missing.jar", metadata: Metadata("missing"), issues: [Issue(ModIssueKind.MissingDependency, "missing.jar", "lib")])));
        Assert.Equal(
            new[] { ModStatusKind.Conflict },
            Kinds(Entry("conflict.jar", metadata: Metadata("conflict"), issues: [Issue(ModIssueKind.Conflict, "conflict.jar", "other")])));
        Assert.Equal(
            new[] { ModStatusKind.PossiblyAbnormal },
            Kinds(Entry("loader.jar", metadata: Metadata("loader"), issues: [Issue(ModIssueKind.LoaderMismatch, "loader.jar", detail: "Forge")])));
        Assert.Equal(
            new[] { ModStatusKind.PossiblyAbnormal },
            Kinds(Entry("duplicate.jar", metadata: Metadata("duplicate"), issues: [Issue(ModIssueKind.DuplicateModId, "duplicate.jar", "dup")])));

        // 危险模组没有数据源（ADR 0043 未接入），保留枚举但不得凭猜测产生命中。
        Assert.DoesNotContain(
            Enumerable.Range(0, 3).SelectMany(index => Kinds(Entry($"mod-{index}.jar", metadata: Metadata($"mod-{index}")))),
            kind => kind == ModStatusKind.Dangerous);
    }

    [Fact]
    public void MultipleStatusesAreAllShownInStableOrderAndNeverExceedFour()
    {
        var item = new ModItemViewModel(
            Entry(
                "broken.jar",
                enabled: false,
                metadata: Metadata("broken"),
                issues:
                [
                    Issue(ModIssueKind.LoaderMismatch, "broken.jar", detail: "Forge"),
                    Issue(ModIssueKind.Conflict, "broken.jar", "other"),
                    Issue(ModIssueKind.MissingDependency, "broken.jar", "lib"),
                ]),
            Localizer());

        Assert.Equal(
            [
                ModStatusKind.PossiblyAbnormal,
                ModStatusKind.Conflict,
                ModStatusKind.MissingDependency,
                ModStatusKind.Disabled,
            ],
            item.StatusBadges.Select(badge => badge.Kind));
        Assert.True(item.StatusBadges.Count <= ModStatusPresentation.MaxBadges);
    }

    [Fact]
    public void ApplicableVersionComesFromTheMinecraftDependencyOnly()
    {
        var withRange = new ModItemViewModel(
            Entry("a.jar", metadata: Metadata("a", minecraftRange: "[1.20.1,1.21)")),
            Localizer());
        Assert.Equal("[1.20.1,1.21) · Fabric", withRange.ApplicableVersionText);

        var withoutRange = new ModItemViewModel(
            Entry("b.jar", metadata: Metadata("b", minecraftRange: null)),
            Localizer());
        Assert.Equal("未知 · Fabric", withoutRange.ApplicableVersionText);

        var unknown = new ModItemViewModel(Entry("c.jar", metadata: null, error: "bad"), Localizer());
        Assert.Equal("未知", unknown.ApplicableVersionText);
    }

    [Fact]
    public async Task SortsByNameAndFileTime()
    {
        var directory = Directory.CreateTempSubdirectory("ecl-mods-sort-");
        try
        {
            var oldest = WriteFile(directory.FullName, "a.jar", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var newest = WriteFile(directory.FullName, "b.jar", new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));
            var middle = WriteFile(directory.FullName, "c.jar", new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));

            var (viewModel, _) = Create(
                Entry("b.jar", path: newest, metadata: Metadata("b")),
                Entry("a.jar", path: oldest, metadata: Metadata("a")),
                Entry("c.jar", path: middle, metadata: Metadata("c")));
            await viewModel.InitializeAsync();

            Assert.Equal(new[] { "a.jar", "b.jar", "c.jar" }, Names(viewModel));

            SelectSort(viewModel, ModSortOrder.NameDescending);
            Assert.Equal(new[] { "c.jar", "b.jar", "a.jar" }, Names(viewModel));

            SelectSort(viewModel, ModSortOrder.TimeAscending);
            Assert.Equal(new[] { "a.jar", "c.jar", "b.jar" }, Names(viewModel));

            SelectSort(viewModel, ModSortOrder.TimeDescending);
            Assert.Equal(new[] { "b.jar", "c.jar", "a.jar" }, Names(viewModel));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task FiltersProduceAccurateCountsAndExplicitCapabilityHints()
    {
        var (viewModel, _) = Create(
            Entry("available.jar", metadata: Metadata("available")),
            Entry("off.jar", enabled: false, metadata: Metadata("off")),
            Entry("missing.jar", metadata: Metadata("missing"), issues: [Issue(ModIssueKind.MissingDependency, "missing.jar", "lib")]));
        await viewModel.InitializeAsync();

        Assert.Equal("总计 3 个模组", viewModel.ResultSummary);

        viewModel.SelectedStatusFilter = Filter(viewModel, ModStatusFilter.Available);
        Assert.Equal("共 1 个结果", viewModel.ResultSummary);
        Assert.Equal(new[] { "available.jar" }, Names(viewModel));
        Assert.True(viewModel.IsFilterActive);

        viewModel.SelectedStatusFilter = Filter(viewModel, ModStatusFilter.Dangerous);
        Assert.Equal("共 0 个结果", viewModel.ResultSummary);
        Assert.Empty(viewModel.FilteredMods);
        Assert.False(viewModel.HasResults);
        Assert.False(string.IsNullOrWhiteSpace(viewModel.CapabilityHint));

        viewModel.ClearStatusFilterCommand.Execute(null);
        Assert.Null(viewModel.SelectedStatusFilter);
        Assert.Equal("总计 3 个模组", viewModel.ResultSummary);
        Assert.Equal(string.Empty, viewModel.CapabilityHint);
    }

    [Fact]
    public async Task SearchOnlyAcceptsEnglishInput()
    {
        var (viewModel, _) = Create(
            Entry("alpha.jar", metadata: Metadata("alpha")),
            Entry("beta.jar", metadata: Metadata("beta")));
        await viewModel.InitializeAsync();

        viewModel.ModSearchText = "alpha";
        viewModel.SearchCommand.Execute(null);

        Assert.False(viewModel.IsSearchInvalid);
        Assert.Equal(new[] { "alpha.jar" }, Names(viewModel));
        Assert.Equal("共 1 个结果", viewModel.ResultSummary);

        viewModel.ModSearchText = "阿尔法";
        viewModel.SearchCommand.Execute(null);

        Assert.True(viewModel.IsSearchInvalid);
        Assert.Equal("alpha", viewModel.AppliedSearchText);
        Assert.Contains("英文", viewModel.StatusMessage);
        Assert.Equal(new[] { "alpha.jar" }, Names(viewModel));
    }

    [Fact]
    public async Task SelectAllInvertAndBulkEnableUseTheDomainService()
    {
        var (viewModel, mods) = Create(
            Entry("alpha.jar", metadata: Metadata("alpha")),
            Entry("beta.jar.disabled", enabled: false, metadata: Metadata("beta")),
            Entry("gamma.jar.disabled", enabled: false, metadata: Metadata("gamma")));
        await viewModel.InitializeAsync();

        viewModel.SelectAllOrInvertCommand.Execute(null);
        Assert.True(viewModel.HasSelection);
        Assert.All(viewModel.FilteredMods, mod => Assert.True(mod.IsSelected));

        await viewModel.EnableSelectedCommand.ExecuteAsync(null);

        Assert.Equal(
            new[] { ("beta.jar.disabled", true), ("gamma.jar.disabled", true) },
            mods.ToggleCalls);
        Assert.Equal("已启用 2 个模组，失败 0 个。", viewModel.StatusMessage);
        Assert.All(viewModel.FilteredMods, mod => Assert.True(mod.Enabled));

        // 刷新后选择被清空：再次全选，然后全部取消。
        viewModel.SelectAllOrInvertCommand.Execute(null);
        Assert.True(viewModel.HasSelection);
        viewModel.SelectAllOrInvertCommand.Execute(null);
        Assert.False(viewModel.HasSelection);
    }

    [Fact]
    public async Task RowToggleAndDeleteGoThroughTheDomainServiceWithConfirmation()
    {
        var dialogs = new FakeDialogService();
        var (viewModel, mods) = Create(
            dialogs,
            Entry("alpha.jar", metadata: Metadata("alpha")));
        await viewModel.InitializeAsync();

        await viewModel.ToggleModCommand.ExecuteAsync(viewModel.FilteredMods[0]);
        Assert.Equal(new[] { ("alpha.jar", false) }, mods.ToggleCalls);
        Assert.Equal(new[] { "alpha.jar.disabled" }, Names(viewModel));

        dialogs.ConfirmResult = false;
        await viewModel.DeleteModCommand.ExecuteAsync(viewModel.FilteredMods[0]);
        Assert.Empty(mods.Deleted);

        dialogs.ConfirmResult = true;
        await viewModel.DeleteModCommand.ExecuteAsync(viewModel.FilteredMods[0]);
        Assert.Equal(new[] { "alpha.jar.disabled" }, mods.Deleted);
        Assert.Empty(viewModel.FilteredMods);
        Assert.False(viewModel.HasResults);
    }

    [Fact]
    public async Task DownloadModsPassesTheStableInstanceAndAppliedQuery()
    {
        var (viewModel, _) = Create(Entry("alpha.jar", metadata: Metadata("alpha")));
        await viewModel.InitializeAsync();

        (string InstanceId, string? Query)? opened = null;
        viewModel.OpenModBrowser = (instanceId, query) => opened = (instanceId, query);

        viewModel.ModSearchText = "alpha";
        viewModel.SearchCommand.Execute(null);
        viewModel.DownloadModsCommand.Execute(null);
        Assert.Equal(("instance-a", (string?)"alpha"), opened!.Value);

        viewModel.ModSearchText = string.Empty;
        viewModel.SearchCommand.Execute(null);
        viewModel.DownloadModsCommand.Execute(null);
        Assert.Equal("instance-a", opened!.Value.InstanceId);
        Assert.Null(opened.Value.Query);
    }

    [Fact]
    public async Task UpdateCommandsAreNeverAvailable()
    {
        var (viewModel, _) = Create(Entry("alpha.jar", metadata: Metadata("alpha")));
        await viewModel.InitializeAsync();

        Assert.False(viewModel.UpdateModCommand.CanExecute(viewModel.FilteredMods[0]));
        Assert.False(viewModel.UpdateSelectedCommand.CanExecute(null));
        Assert.Contains("更新来源未实现", viewModel.UpdateUnavailableHint);

        viewModel.UpdateSelectedCommand.Execute(null);
    }

    [Fact]
    public void PackNamesCollapseMultilineDescriptions()
    {
        var entry = new ContentPackEntry(
            "fabric.jar",
            @"C:\instance\resourcepacks\fabric.jar",
            ContentPackKind.ResourcePack,
            "{\"bold\":false,\r\n\"text\":\"fabric\"}",
            null,
            null,
            Enabled: true,
            Incompatible: false);

        var item = new ContentPackItemViewModel(entry, Localizer());

        Assert.DoesNotContain('\n', item.Name);
        Assert.DoesNotContain('\r', item.Name);
        Assert.Contains("fabric", item.Name);
    }

    [Fact]
    public async Task DisposeStopsFurtherScans()
    {
        var (viewModel, mods) = Create(Entry("alpha.jar", metadata: Metadata("alpha")));
        viewModel.Dispose();

        await viewModel.InitializeAsync();

        Assert.True(viewModel.IsDisposed);
        Assert.Equal(0, mods.ScanCount);
        Assert.Empty(viewModel.Mods);
    }

    private static (ModsViewModel ViewModel, FakeModService Mods) Create(params ModEntry[] entries) =>
        Create(new FakeDialogService(), entries);

    private static (ModsViewModel ViewModel, FakeModService Mods) Create(
        FakeDialogService dialogs,
        params ModEntry[] entries)
    {
        var mods = new FakeModService(entries);
        var viewModel = new ModsViewModel(
            mods,
            new FakeModpackService(),
            new FakeDependencyInstaller(),
            new FakeContentPackService(),
            dialogs,
            Localizer(),
            "instance-a",
            "Instance A");
        return (viewModel, mods);
    }

    private static IReadOnlyList<ModStatusKind> Kinds(ModEntry entry) =>
        new ModItemViewModel(entry, Localizer()).StatusBadges.Select(badge => badge.Kind).ToArray();

    private static ModStatusFilterChoice Filter(ModsViewModel viewModel, ModStatusFilter filter) =>
        viewModel.StatusFilters.Single(choice => choice.Filter == filter);

    private static void SelectSort(ModsViewModel viewModel, ModSortOrder order) =>
        viewModel.SelectedSortOrder = viewModel.SortOrders.Single(choice => choice.Order == order);

    private static string[] Names(ModsViewModel viewModel) =>
        viewModel.FilteredMods.Select(mod => mod.FileName).ToArray();

    private static string WriteFile(string directory, string fileName, DateTime lastWrite)
    {
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, "stub");
        File.SetLastWriteTimeUtc(path, lastWrite);
        return path;
    }

    private static ModEntry Entry(
        string fileName,
        bool enabled = true,
        ModMetadata? metadata = null,
        string? path = null,
        string? error = null,
        IReadOnlyList<ModIssue>? issues = null) =>
        new(
            fileName,
            path ?? Path.Combine(Path.GetTempPath(), "ecl-mods-tests", fileName),
            enabled,
            metadata,
            error,
            issues ?? []);

    private static ModMetadata Metadata(
        string modId,
        ModPlatform platform = ModPlatform.Fabric,
        string? minecraftRange = "1.20.1") =>
        new(
            modId,
            modId,
            "1.0",
            null,
            platform,
            minecraftRange is null ? [] : [new ModDependency("minecraft", true, minecraftRange)],
            [],
            null);

    private static ModIssue Issue(
        ModIssueKind kind,
        string fileName,
        string? related = null,
        string? detail = null) =>
        new(kind, fileName, fileName.Replace(".jar", string.Empty, StringComparison.OrdinalIgnoreCase), related, detail);

    private sealed class FakeModService(IEnumerable<ModEntry> entries) : IModService
    {
        private readonly List<ModEntry> _entries = [.. entries];

        public int ScanCount { get; private set; }

        public List<(string FileName, bool Enabled)> ToggleCalls { get; } = [];

        public List<string> Deleted { get; } = [];

        public Task<Result<ModScanReport>> ScanAsync(
            string instanceId,
            CancellationToken cancellationToken = default)
        {
            ScanCount++;
            var snapshot = _entries.ToArray();
            return Task.FromResult(Result<ModScanReport>.Ok(new ModScanReport(
                instanceId,
                "mods",
                snapshot,
                snapshot.SelectMany(entry => entry.Issues).ToArray())));
        }

        public Task<Result> SetEnabledAsync(
            string instanceId,
            string fileName,
            bool enabled,
            CancellationToken cancellationToken = default)
        {
            ToggleCalls.Add((fileName, enabled));
            var index = _entries.FindIndex(entry =>
                string.Equals(entry.FileName, fileName, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                var current = _entries[index];
                const string suffix = ".disabled";
                var isDisabled = current.FileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
                var newName = enabled
                    ? isDisabled ? current.FileName[..^suffix.Length] : current.FileName
                    : isDisabled ? current.FileName : current.FileName + suffix;
                _entries[index] = current with { FileName = newName, Enabled = enabled };
            }

            return Task.FromResult(Result.Ok());
        }

        public Task<Result<ModBatchResult>> SetAllEnabledAsync(
            string instanceId,
            bool enabled,
            CancellationToken cancellationToken = default)
        {
            foreach (var entry in _entries.ToArray())
            {
                _ = SetEnabledAsync(instanceId, entry.FileName, enabled, cancellationToken);
            }

            return Task.FromResult(Result<ModBatchResult>.Ok(new ModBatchResult(_entries.Count, [])));
        }

        public Task<Result<ModBatchResult>> ImportAsync(
            string instanceId,
            IReadOnlyList<string> sourcePaths,
            bool overwrite = false,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<ModBatchResult>.Ok(new ModBatchResult(0, [])));

        public Task<Result> DeleteAsync(
            string instanceId,
            string fileName,
            CancellationToken cancellationToken = default)
        {
            Deleted.Add(fileName);
            _entries.RemoveAll(entry => string.Equals(entry.FileName, fileName, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(Result.Ok());
        }
    }

    private sealed class FakeModpackService : IModpackService
    {
        public Task<Result<ModpackImportReport>> ImportAsync(
            string instanceId,
            string archivePath,
            IProgress<ModpackProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<ModpackImportReport>.Ok(new ModpackImportReport("pack", null, 0, 0, 0, [])));

        public Task<Result<ModpackExportReport>> ExportAsync(
            string instanceId,
            string outputPath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<ModpackExportReport>.Ok(new ModpackExportReport(outputPath, 0, false)));
    }

    private sealed class FakeDependencyInstaller : IModDependencyInstaller
    {
        public Task<Result<ModDependencyInstallReport>> InstallMissingAsync(
            string instanceId,
            IProgress<ModDependencyProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<ModDependencyInstallReport>.Ok(
                new ModDependencyInstallReport([], [], [], [])));
    }

    private sealed class FakeContentPackService : IContentPackService
    {
        public Task<Result<ContentPackList>> ListAsync(
            string instanceId,
            ContentPackKind kind,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<ContentPackList>.Ok(new ContentPackList(
                [],
                [],
                [],
                [],
                PackOptionsStatus.NotInitialized,
                null)));

        public Task<Result<ContentPackEntry>> ImportAsync(
            string instanceId,
            ContentPackKind kind,
            string sourcePath,
            bool overwrite = false,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result> DeleteAsync(
            string instanceId,
            ContentPackKind kind,
            string fileName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Ok());

        public Task<Result> SetEnabledAsync(
            string instanceId,
            ContentPackKind kind,
            string fileName,
            bool enabled,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Ok());
    }

    private sealed class FakeDialogService : IDialogService
    {
        public bool ConfirmResult { get; set; } = true;

        public IReadOnlyList<string> OpenFiles { get; set; } = [];

        public bool Confirm(string title, string message, string? acknowledgementToken = null) => ConfirmResult;

        public InstanceDeletionChoice ChooseInstanceDeletion(string title, string message, bool canDeleteManagedData) =>
            InstanceDeletionChoice.Cancel;

        public void ShowInfo(string title, string message)
        {
        }

        public void ShowError(string title, string message)
        {
        }

        public string? PickOpenFile(string title, string filter) => null;

        public IReadOnlyList<string> PickOpenFiles(string title, string filter) => OpenFiles;

        public string? PickSaveFile(string title, string filter, string? defaultFileName = null) => null;

        public string? PickFolder(string title) => null;

        public bool OpenUrl(string url) => false;
    }
}




