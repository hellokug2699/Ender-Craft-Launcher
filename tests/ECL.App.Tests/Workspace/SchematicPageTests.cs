// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.IO;
using Ecl.App.Services;
using Ecl.App.Workspace.ViewModels;
using Ecl.Core;
using Ecl.Core.Localization;
using Ecl.Minecraft.Schematics;
using Xunit;

namespace Ecl.App.Tests.Workspace;

/// <summary>
/// 投影页（B2c，schematics.md）：列表字段、按格式的支持状态、检测失败仍可管理文件、
/// 选择与删除确认，以及不可用的查看工具不被伪装成可用。
/// </summary>
public sealed class SchematicPageTests
{
    private static ILocalizer Localizer() => new JsonLocalizer(CultureInfo.GetCultureInfo("zh-CN"));

    [Fact]
    public async Task LoadsFilesWithFormatAndRecognitionInformation()
    {
        var service = new FakeSchematicService(
            Entry("zeta.litematic", SchematicFormat.Litematica, SchematicRecognition.Recognized),
            Entry("alpha.schem", SchematicFormat.SpongeSchematic, SchematicRecognition.Unverified),
            Entry("broken.bp", SchematicFormat.AxiomBlueprint, SchematicRecognition.Unreadable));
        var page = Create(service);

        await page.InitializeAsync();

        Assert.Equal(["alpha.schem", "broken.bp", "zeta.litematic"], page.Items.Select(item => item.FileName));
        Assert.Equal("共 3 个", page.CountText);
        Assert.Equal("Sponge 投影", page.Items[0].FormatText);
        Assert.Equal("格式未验证", page.Items[0].RecognitionText);
        Assert.Equal("无法读取", page.Items[1].RecognitionText);
        Assert.Equal("Axiom", page.Items[1].SourceText);
        Assert.False(page.IsEmpty);
    }

    [Fact]
    public async Task SupportResultIsProjectedPerFormatWithoutFlatteningStates()
    {
        var support = new SchematicSupportResult(
            SchematicSupportState.Supported,
            [
                new SchematicFormatSupport(SchematicFormat.Litematica, SchematicSupportState.Supported, ["litematica"]),
                new SchematicFormatSupport(SchematicFormat.SpongeSchematic, SchematicSupportState.Incomplete, ["worldedit"], "malilib"),
                new SchematicFormatSupport(SchematicFormat.MceSchematic, SchematicSupportState.Incompatible, ["worldedit"], "artifact-mismatch"),
                new SchematicFormatSupport(SchematicFormat.NbtStructure, SchematicSupportState.Unknown, ["create"], "artifact-unverified"),
                new SchematicFormatSupport(SchematicFormat.AxiomBlueprint, SchematicSupportState.Missing, []),
            ],
            ["litematica", "worldedit", "create"]);
        var page = Create(new FakeSchematicService(), support: new FakeSchematicSupportService(support));

        await page.InitializeAsync();

        Assert.Equal(SchematicSupportState.Supported, page.SupportState);
        Assert.Equal("支持", page.SupportStateText);
        Assert.Contains("litematica", page.SupportProvidersText);
        Assert.Empty(page.SupportError);
        Assert.Equal(5, page.FormatSupport.Count);
        Assert.Equal("缺失", page.FormatSupport[4].StateText);
        Assert.Contains("malilib", page.FormatSupport[1].DetailText);
        Assert.Contains("不匹配", page.FormatSupport[2].DetailText);
        Assert.True(page.HasSupportDetail);
        Assert.Equal(ContentPackStatusKind.Corrupt, page.FormatSupport[2].StatusKind);
    }

    [Fact]
    public async Task SupportFailureDoesNotBlockFileManagement()
    {
        var page = Create(
            new FakeSchematicService(Entry("alpha.litematic", SchematicFormat.Litematica, SchematicRecognition.Recognized)),
            support: new FakeSchematicSupportService(null, fail: true));

        await page.InitializeAsync();

        Assert.Single(page.Items);
        Assert.NotEmpty(page.SupportError);
        Assert.Null(page.SupportState);
        Assert.Empty(page.FormatSupport);
    }

    [Fact]
    public async Task DeleteSubmitsSelectedPathsAndReloadsTheList()
    {
        var service = new FakeSchematicService(
            Entry("alpha.litematic", SchematicFormat.Litematica, SchematicRecognition.Recognized),
            Entry("beta.schem", SchematicFormat.SpongeSchematic, SchematicRecognition.Recognized));
        var dialogs = new FakeDialogService();
        var page = Create(service, dialogs: dialogs);
        await page.InitializeAsync();
        page.Items[0].IsSelected = true;
        page.Items[1].IsSelected = true;

        await page.DeleteSelectedCommand.ExecuteAsync(null);

        var batch = Assert.Single(service.DeleteBatches);
        Assert.Equal(
            ["schematics/alpha.litematic", "schematics/beta.schem"],
            batch.Select(path => path.Replace('\\', '/')));
        Assert.Empty(page.Items);
        Assert.Contains("已删除 2", page.StatusMessage);
    }

    [Fact]
    public async Task DeleteCancelNeverCallsTheDomain()
    {
        var service = new FakeSchematicService(
            Entry("alpha.litematic", SchematicFormat.Litematica, SchematicRecognition.Recognized));
        var dialogs = new FakeDialogService { ConfirmResult = false };
        var page = Create(service, dialogs: dialogs);
        await page.InitializeAsync();
        page.Items[0].IsSelected = true;

        await page.DeleteSelectedCommand.ExecuteAsync(null);

        Assert.Empty(service.DeleteBatches);
        Assert.Single(page.Items);
        Assert.True(page.Items[0].IsSelected);
    }

    [Fact]
    public async Task DeleteFailureKeepsRowsAndSelection()
    {
        var service = new FakeSchematicService(
            Entry("alpha.litematic", SchematicFormat.Litematica, SchematicRecognition.Recognized))
        {
            DeleteFails = true,
        };
        var page = Create(service);
        await page.InitializeAsync();
        page.Items[0].IsSelected = true;

        await page.DeleteSelectedCommand.ExecuteAsync(null);

        Assert.Single(page.Items);
        Assert.True(page.Items[0].IsSelected);
        Assert.Contains("失败", page.StatusMessage);
    }

    [Fact]
    public async Task UnavailableViewingToolsAreNotPresentedAsWorkingEntries()
    {
        var page = Create(new FakeSchematicService());

        await page.InitializeAsync();

        Assert.Contains("NBT Explorer", page.NbtExplorerHint);
        Assert.Contains("Addition", page.NbtExplorerHint);
        Assert.Contains("G-03", page.NbtExplorerHint);
        Assert.Contains("三维预览", page.RendererTitle);
        Assert.Contains("Addition", page.RendererHint);
    }

    [Fact]
    public async Task ListFailureShowsErrorInsteadOfEmptyState()
    {
        var service = new FakeSchematicService
        {
            ListFails = true,
        };
        var page = Create(service);

        await page.InitializeAsync();

        Assert.Empty(page.Items);
        Assert.NotEmpty(page.ErrorMessage);
        Assert.False(page.IsEmpty);
    }

    private static SchematicPageViewModel Create(
        FakeSchematicService service,
        FakeDialogService? dialogs = null,
        FakeSchematicSupportService? support = null) =>
        new(
            "instance-1",
            service,
            support ?? new FakeSchematicSupportService(EmptySupport()),
            dialogs ?? new FakeDialogService(),
            Localizer());

    private static SchematicSupportResult EmptySupport() =>
        new(
            SchematicSupportState.Missing,
            Enum.GetValues<SchematicFormat>()
                .Select(format => new SchematicFormatSupport(format, SchematicSupportState.Missing, []))
                .ToArray(),
            []);

    private static SchematicEntry Entry(
        string name,
        SchematicFormat format,
        SchematicRecognition recognition) =>
        new(
            name,
            format == SchematicFormat.AxiomBlueprint
                ? Path.Combine("config", "axiom", "blueprints", name)
                : Path.Combine("schematics", name),
            format,
            recognition,
            2048,
            DateTimeOffset.UtcNow);

    private sealed class FakeSchematicService(params SchematicEntry[] entries) : ISchematicService
    {
        private readonly List<SchematicEntry> _entries = [.. entries];

        public List<List<string>> DeleteBatches { get; } = [];

        public bool ListFails { get; set; }

        public bool DeleteFails { get; set; }

        public Task<Result<SchematicList>> ListAsync(
            string instanceId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ListFails
                ? Result<SchematicList>.Fail("schematics.list_failed", "读取投影文件列表失败。")
                : Result<SchematicList>.Ok(new SchematicList([.. _entries], 0)));

        public Task<Result<SchematicDeleteReport>> DeleteAsync(
            string instanceId,
            IReadOnlyList<string> relativePaths,
            CancellationToken cancellationToken = default)
        {
            DeleteBatches.Add([.. relativePaths]);
            if (DeleteFails)
            {
                return Task.FromResult(Result<SchematicDeleteReport>.Fail(
                    "schematics.delete_failed",
                    "删除投影文件失败。"));
            }

            var deleted = new HashSet<string>(relativePaths, StringComparer.Ordinal);
            _entries.RemoveAll(entry => deleted.Contains(entry.RelativePath));
            return Task.FromResult(Result<SchematicDeleteReport>.Ok(
                new SchematicDeleteReport([.. relativePaths], RecoveryRequired: false)));
        }
    }

    private sealed class FakeSchematicSupportService(
        SchematicSupportResult? result,
        bool fail = false) : ISchematicSupportService
    {
        public Task<Result<SchematicSupportResult>> DetectAsync(
            string instanceId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(fail || result is null
                ? Result<SchematicSupportResult>.Fail("schematics.detect_failed", "检测失败。")
                : Result<SchematicSupportResult>.Ok(result));
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
