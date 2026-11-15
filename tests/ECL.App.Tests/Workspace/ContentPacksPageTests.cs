// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.IO;
using Ecl.App.Services;
using Ecl.App.Workspace.ViewModels;
using Ecl.Core;
using Ecl.Core.Localization;
using Ecl.Minecraft.Mods;
using Xunit;

namespace Ecl.App.Tests.Workspace;

/// <summary>
/// 资源包/光影包页（B2b，resource-library-template.md）：双表与独立计数、
/// 拖动/按钮的待应用状态、一次提交的启停与顺序、删除确认、光影包单选语义，
/// 以及状态/版本范围只来自真实数据。
/// </summary>
public sealed class ContentPacksPageTests
{
    private static ILocalizer Localizer() => new JsonLocalizer(CultureInfo.GetCultureInfo("zh-CN"));

    [Fact]
    public async Task SplitsEnabledAndDisabledTablesWithIndependentCounts()
    {
        var packs = new FakeContentPackService(
            Pack("alpha.zip", enabled: true),
            Pack("beta.zip", enabled: true),
            Pack("gamma.zip", enabled: false));
        var page = Create(packs);
        await page.InitializeAsync();

        Assert.Equal(["alpha.zip", "beta.zip"], RowNames(page.EnabledPacks));
        Assert.Equal(["gamma.zip"], RowNames(page.DisabledPacks));
        Assert.Equal("共 2 个", page.EnabledCountText);
        Assert.Equal("共 1 个", page.DisabledCountText);
        Assert.True(page.HasAnyPacks);
        Assert.False(page.HasPendingChanges);
        Assert.False(page.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public async Task DragKeepsChangesPendingUntilApplySubmitsTheOrderedList()
    {
        var packs = new FakeContentPackService(
            Pack("alpha.zip", enabled: true),
            Pack("beta.zip", enabled: false),
            Pack("gamma.zip", enabled: false));
        var page = Create(packs);
        await page.InitializeAsync();

        // 跨表启用 + 同表排序都只改工作副本。
        page.MovePack(page.DisabledPacks[0], toEnabled: true, page.EnabledPacks.Count);
        page.MoveWithin(page.EnabledPacks[0], enabledTable: true, targetIndex: 1);

        Assert.Equal(["beta.zip", "alpha.zip"], RowNames(page.EnabledPacks));
        Assert.True(page.HasPendingChanges);
        Assert.True(page.ApplyCommand.CanExecute(null));
        Assert.Empty(packs.AppliedOrders);

        await page.ApplyCommand.ExecuteAsync(null);

        var applied = Assert.Single(packs.AppliedOrders);
        Assert.Equal(["beta.zip", "alpha.zip"], applied);
        Assert.False(page.HasPendingChanges);
        Assert.Equal(["beta.zip", "alpha.zip"], RowNames(page.EnabledPacks));
        Assert.Equal(["gamma.zip"], RowNames(page.DisabledPacks));
    }

    [Fact]
    public async Task ToggleButtonIsTheKeyboardEquivalentOfCrossTableDrag()
    {
        var packs = new FakeContentPackService(
            Pack("alpha.zip", enabled: true),
            Pack("beta.zip", enabled: false));
        var page = Create(packs);
        await page.InitializeAsync();

        Assert.Equal("禁用", page.EnabledPacks[0].ToggleLabel);
        page.TogglePackCommand.Execute(page.EnabledPacks[0]);

        Assert.Empty(page.EnabledPacks);
        Assert.Equal(2, page.DisabledPacks.Count);
        Assert.Equal("启用", page.DisabledPacks[0].ToggleLabel);
        Assert.True(page.HasPendingChanges);
    }

    [Fact]
    public async Task ShaderPacksKeepExactlyOneActiveEntry()
    {
        var packs = new FakeContentPackService(
            Pack("first.zip", enabled: true, kind: ContentPackKind.ShaderPack),
            Pack("second.zip", enabled: false, kind: ContentPackKind.ShaderPack),
            Pack("third.zip", enabled: false, kind: ContentPackKind.ShaderPack));
        var page = Create(packs, ContentPackKind.ShaderPack);
        await page.InitializeAsync();

        Assert.True(page.IsShaderPage);
        Assert.Single(page.EnabledPacks);

        page.MovePack(page.DisabledPacks.Single(pack => pack.FileName == "third.zip"), toEnabled: true, 0);

        var active = Assert.Single(page.EnabledPacks);
        Assert.Equal("third.zip", active.FileName);
        // 原来启用的光影包退回未启用表，而不是同时启用两个。
        Assert.Contains(page.DisabledPacks, pack => pack.FileName == "first.zip");
        Assert.True(page.HasPendingChanges);

        await page.ApplyCommand.ExecuteAsync(null);
        var applied = Assert.Single(packs.AppliedOrders);
        Assert.Equal(["third.zip"], applied);
    }

    [Fact]
    public async Task DeleteAsksForConfirmationAndReportsTheDomainResult()
    {
        var packs = new FakeContentPackService(Pack("alpha.zip", enabled: false));
        var dialogs = new FakeDialogService();
        var page = Create(packs, dialogs: dialogs);
        await page.InitializeAsync();

        dialogs.ConfirmResult = false;
        await page.DeletePackCommand.ExecuteAsync(page.DisabledPacks[0]);
        Assert.Empty(packs.DeletedFiles);

        dialogs.ConfirmResult = true;
        await page.DeletePackCommand.ExecuteAsync(page.DisabledPacks[0]);

        Assert.Equal(["alpha.zip"], packs.DeletedFiles);
        Assert.Empty(page.DisabledPacks);
        Assert.Contains("已删除", page.StatusMessage);
    }

    [Fact]
    public async Task CorruptAndUnrecognizedPacksMapToTheirOwnStatuses()
    {
        var packs = new FakeContentPackService(
            Pack("active.zip", enabled: true),
            Pack("corrupt.zip", enabled: true, readState: ContentPackReadState.Corrupt),
            Pack("unrecognized.zip", enabled: false, readState: ContentPackReadState.Unrecognized),
            Pack("incompatible.zip", enabled: true, incompatible: true));
        var page = Create(packs);
        await page.InitializeAsync();

        ContentPackRowViewModel Row(string fileName) =>
            page.EnabledPacks.Concat(page.DisabledPacks).Single(pack => pack.FileName == fileName);

        Assert.Equal(ContentPackStatusKind.Active, Row("active.zip").StatusKind);
        Assert.Equal(ContentPackStatusKind.Corrupt, Row("corrupt.zip").StatusKind);
        Assert.Equal(ContentPackStatusKind.Unrecognized, Row("unrecognized.zip").StatusKind);
        Assert.Equal(ContentPackStatusKind.Warning, Row("incompatible.zip").StatusKind);
    }

    [Fact]
    public async Task VersionRangeOnlyShowsDeclaredOrOfficiallyKnownFormats()
    {
        var packs = new FakeContentPackService(
            Pack("declared.zip", enabled: true, supportedFormats: new PackFormatRange(15, 34)),
            Pack("known.zip", enabled: true, packFormat: 15),
            Pack("future.zip", enabled: true, packFormat: 99),
            Pack("no-metadata.zip", enabled: true, packFormat: null, readState: ContentPackReadState.Unrecognized));
        var page = Create(packs);
        await page.InitializeAsync();

        string Range(string fileName) =>
            page.EnabledPacks.Single(pack => pack.FileName == fileName).VersionRangeText;

        Assert.Equal("1.20 – 1.21.1", Range("declared.zip"));
        Assert.Equal("1.20 – 1.20.1", Range("known.zip"));
        Assert.Equal("格式 99", Range("future.zip"));
        Assert.Equal("未知", Range("no-metadata.zip"));
    }

    [Fact]
    public async Task UnreadableOptionsAreSurfacedAndBlockApply()
    {
        var packs = new FakeContentPackService(Pack("alpha.zip", enabled: true))
        {
            OptionsStatus = PackOptionsStatus.Unreadable,
            Warning = "无法解析 options.txt",
        };
        var page = Create(packs);
        await page.InitializeAsync();

        Assert.Contains("无法解析", page.WarningText);

        page.MovePack(page.EnabledPacks[0], toEnabled: false, 0);
        await page.ApplyCommand.ExecuteAsync(null);

        Assert.Empty(packs.AppliedOrders);
        Assert.Contains("无法解析", page.StatusMessage);
        Assert.True(page.HasPendingChanges);
    }

    [Fact]
    public async Task DisposedPageStopsScanning()
    {
        var packs = new FakeContentPackService(Pack("alpha.zip", enabled: true));
        var page = Create(packs);
        page.Dispose();

        await page.InitializeAsync();

        Assert.True(page.IsDisposed);
        Assert.Equal(0, packs.ListCount);
    }

    [Fact]
    public async Task ShaderBannerShowsUntilDismissedAndThenFallsBackToGrayHint()
    {
        var packs = new FakeContentPackService(Pack("alpha.zip", enabled: true, kind: ContentPackKind.ShaderPack));
        var hints = new FakeShaderHintStore();
        var shaderSupport = new FakeShaderSupportService(ShaderSupportState.Missing);
        var page = Create(packs, ContentPackKind.ShaderPack, hints: hints, shaderSupport: shaderSupport);
        await page.InitializeAsync();

        Assert.True(page.IsShaderBannerVisible);
        Assert.False(page.IsShaderHintVisible);
        Assert.Contains("没有检测到", page.ShaderSupportReason);

        page.DismissShaderBannerCommand.Execute(null);

        Assert.True(hints.IsDismissed("instance-a"));
        Assert.False(page.IsShaderBannerVisible);
        Assert.True(page.IsShaderHintVisible);

        // 重新进入页面时按已保存的提示状态显示灰色弱提示。
        var reopened = Create(packs, ContentPackKind.ShaderPack, hints: hints, shaderSupport: shaderSupport);
        await reopened.InitializeAsync();
        Assert.False(reopened.IsShaderBannerVisible);
        Assert.True(reopened.IsShaderHintVisible);
    }

    [Fact]
    public async Task SupportedShaderComponentHidesBothPrompts()
    {
        var packs = new FakeContentPackService(Pack("alpha.zip", enabled: true, kind: ContentPackKind.ShaderPack));
        var hints = new FakeShaderHintStore();
        var page = Create(
            packs,
            ContentPackKind.ShaderPack,
            hints: hints,
            shaderSupport: new FakeShaderSupportService(ShaderSupportState.Supported));
        await page.InitializeAsync();

        Assert.Equal(ShaderSupportState.Supported, page.ShaderSupport);
        Assert.False(page.IsShaderBannerVisible);
        Assert.False(page.IsShaderHintVisible);
        Assert.Empty(page.ShaderSupportReason);
    }

    [Fact]
    public async Task ClearingAllShaderPacksResetsTheDismissedHint()
    {
        var packs = new FakeContentPackService(Pack("alpha.zip", enabled: true, kind: ContentPackKind.ShaderPack));
        var hints = new FakeShaderHintStore();
        hints.Dismiss("instance-a");
        var page = Create(
            packs,
            ContentPackKind.ShaderPack,
            hints: hints,
            shaderSupport: new FakeShaderSupportService(ShaderSupportState.Missing));
        await page.InitializeAsync();
        Assert.True(page.IsShaderHintVisible);

        // 删除最后一个光影包后，下一次进入页面重新显示首次提示（shaders.md §5）。
        await page.DeletePackCommand.ExecuteAsync(page.EnabledPacks[0]);
        Assert.Empty(page.EnabledPacks);
        Assert.False(hints.IsDismissed("instance-a"));

        var reopened = Create(
            packs,
            ContentPackKind.ShaderPack,
            hints: hints,
            shaderSupport: new FakeShaderSupportService(ShaderSupportState.Missing));
        await reopened.InitializeAsync();
        Assert.True(reopened.IsShaderBannerVisible);
        Assert.False(reopened.IsShaderHintVisible);

        // 首次检测到“数量为 0”本身不重置状态，只有从非零变为零才重置。
        Assert.False(hints.IsDismissed("instance-a"));
    }

    [Fact]
    public async Task FailedDetectionIsSurfacedInsteadOfClaimingSupport()
    {
        var packs = new FakeContentPackService(Pack("alpha.zip", enabled: true, kind: ContentPackKind.ShaderPack));
        var page = Create(
            packs,
            ContentPackKind.ShaderPack,
            hints: new FakeShaderHintStore(),
            shaderSupport: new FakeShaderSupportService(null, fail: true));
        await page.InitializeAsync();

        Assert.Null(page.ShaderSupport);
        Assert.False(page.IsShaderBannerVisible);
        Assert.False(page.IsShaderHintVisible);
        Assert.Contains("扫描失败", page.ShaderSupportError);
    }

    [Fact]
    public async Task IncompleteAndConflictStatesExplainTheRealCondition()
    {
        var packs = new FakeContentPackService(Pack("alpha.zip", enabled: true, kind: ContentPackKind.ShaderPack));

        var incomplete = Create(
            packs,
            ContentPackKind.ShaderPack,
            hints: new FakeShaderHintStore(),
            shaderSupport: new FakeShaderSupportService(
                ShaderSupportState.Incomplete,
                [ShaderSupportDetector.OculusModId],
                "embeddium"));
        await incomplete.InitializeAsync();
        Assert.Contains("Oculus", incomplete.ShaderSupportReason);
        Assert.Contains("embeddium", incomplete.ShaderSupportReason);

        var conflict = Create(
            packs,
            ContentPackKind.ShaderPack,
            hints: new FakeShaderHintStore(),
            shaderSupport: new FakeShaderSupportService(
                ShaderSupportState.Conflict,
                [ShaderSupportDetector.IrisModId, ShaderSupportDetector.OptiFineProvider]));
        await conflict.InitializeAsync();
        Assert.Contains("Iris", conflict.ShaderSupportReason);
        Assert.Contains("OptiFine", conflict.ShaderSupportReason);
    }

    private static string[] RowNames(IEnumerable<ContentPackRowViewModel> rows) =>
        rows.Select(row => row.FileName).ToArray();

    private static ContentPackPageViewModel Create(
        FakeContentPackService packs,
        ContentPackKind kind = ContentPackKind.ResourcePack,
        FakeDialogService? dialogs = null,
        FakeShaderHintStore? hints = null,
        FakeShaderSupportService? shaderSupport = null) =>
        new(
            "instance-a",
            kind,
            packs,
            dialogs ?? new FakeDialogService(),
            Localizer(),
            shaderSupport,
            hints);

    private static ContentPackEntry Pack(
        string fileName,
        bool enabled,
        ContentPackKind kind = ContentPackKind.ResourcePack,
        int? packFormat = 15,
        PackFormatRange? supportedFormats = null,
        bool incompatible = false,
        ContentPackReadState readState = ContentPackReadState.Ok) =>
        new(
            fileName,
            $@"C:\instance\{fileName}.zip",
            kind,
            Path.GetFileNameWithoutExtension(fileName),
            null,
            packFormat,
            enabled,
            incompatible)
        {
            ReadState = readState,
            SupportedFormats = supportedFormats,
        };

    private sealed class FakeContentPackService(params ContentPackEntry[] entries) : IContentPackService
    {
        private readonly List<ContentPackEntry> _entries = [.. entries];

        public int ListCount { get; private set; }

        public List<string[]> AppliedOrders { get; } = [];

        public List<string> DeletedFiles { get; } = [];

        public PackOptionsStatus OptionsStatus { get; init; } = PackOptionsStatus.Valid;

        public string? Warning { get; init; }

        public Task<Result<ContentPackList>> ListAsync(
            string instanceId,
            ContentPackKind kind,
            CancellationToken cancellationToken = default)
        {
            ListCount++;
            return Task.FromResult(Result<ContentPackList>.Ok(new ContentPackList(
                _entries.ToArray(),
                [],
                [],
                [],
                OptionsStatus,
                Warning)));
        }

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
            CancellationToken cancellationToken = default)
        {
            DeletedFiles.Add(fileName);
            _entries.RemoveAll(entry => entry.FileName == fileName);
            return Task.FromResult(Result.Ok());
        }

        public Task<Result> SetEnabledAsync(
            string instanceId,
            ContentPackKind kind,
            string fileName,
            bool enabled,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Ok());

        public Task<Result> ApplyAsync(
            string instanceId,
            ContentPackKind kind,
            IReadOnlyList<string> orderedFileNames,
            CancellationToken cancellationToken = default)
        {
            if (OptionsStatus == PackOptionsStatus.Unreadable)
            {
                return Task.FromResult(Result.Fail("options.invalid_value", "无法解析 options.txt"));
            }

            AppliedOrders.Add([.. orderedFileNames]);
            var enabledSet = new HashSet<string>(orderedFileNames, StringComparer.OrdinalIgnoreCase);
            var byName = _entries.ToDictionary(entry => entry.FileName, StringComparer.OrdinalIgnoreCase);

            // 与领域一致：已启用条目按提交顺序排列，其余条目接在后面。
            var reordered = orderedFileNames
                .Select(name => byName[name] with { Enabled = true })
                .Concat(_entries
                    .Where(entry => !enabledSet.Contains(entry.FileName))
                    .Select(entry => entry with { Enabled = false }))
                .ToArray();
            _entries.Clear();
            _entries.AddRange(reordered);

            return Task.FromResult(Result.Ok());
        }
    }

    private sealed class FakeShaderHintStore : IShaderHintStore
    {
        private readonly HashSet<string> _dismissed = new(StringComparer.Ordinal);

        public bool IsDismissed(string instanceId) => _dismissed.Contains(instanceId);

        public void Dismiss(string instanceId) => _dismissed.Add(instanceId);

        public void Reset(string instanceId) => _dismissed.Remove(instanceId);
    }

    private sealed class FakeShaderSupportService(
        ShaderSupportState? state,
        IReadOnlyList<string>? providers = null,
        string? detail = null,
        bool fail = false) : IShaderSupportService
    {
        public Task<Result<ShaderSupportResult>> DetectAsync(
            string instanceId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(fail
                ? Result<ShaderSupportResult>.Fail("mods.scan_failed", "扫描失败")
                : Result<ShaderSupportResult>.Ok(new ShaderSupportResult(
                    state ?? ShaderSupportState.Missing,
                    providers ?? [],
                    detail)));
    }

    private sealed class FakeDialogService : IDialogService
    {
        public bool ConfirmResult { get; set; } = true;

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

        public IReadOnlyList<string> PickOpenFiles(string title, string filter) => [];

        public string? PickSaveFile(string title, string filter, string? defaultFileName = null) => null;

        public string? PickFolder(string title) => null;

        public bool OpenUrl(string url) => false;
    }
}
