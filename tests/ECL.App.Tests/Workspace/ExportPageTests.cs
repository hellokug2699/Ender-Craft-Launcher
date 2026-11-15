// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.IO;
using Ecl.App.Services;
using Ecl.App.Workspace.ViewModels;
using Ecl.Core;
using Ecl.Core.Localization;
using Ecl.Minecraft.Export;
using Xunit;

namespace Ecl.App.Tests.Workspace;

/// <summary>
/// 导出页（B2c，export.md）：三种方式、真实分类体积、风险确认、诊断包说明、
/// 进度/结果/跳过项呈现，以及取消/失败不得显示为成功。
/// </summary>
public sealed class ExportPageTests
{
    private static ILocalizer Localizer() => new JsonLocalizer(CultureInfo.GetCultureInfo("zh-CN"));

    [Fact]
    public async Task LoadsPlanAndShowsFullExportEstimate()
    {
        var service = new FakeExportService(Plan());
        var page = Create(service);

        await page.InitializeAsync();

        Assert.True(page.IsFullMode);
        Assert.Equal(2, page.Categories.Count);
        Assert.Contains("预计大小", page.EstimateText);
        Assert.Contains("3", page.EstimateText);
        Assert.Equal("共 3 个文件", page.SelectedFilesText);
        Assert.True(page.ShowCategories);
        Assert.True(page.StartExportCommand.CanExecute(null));
        Assert.Empty(page.ErrorMessage);
    }

    [Fact]
    public async Task CustomModeSelectionDrivesTheEstimate()
    {
        var service = new FakeExportService(Plan());
        var page = Create(service);
        await page.InitializeAsync();

        page.UseCustomModeCommand.Execute(null);
        Assert.True(page.IsCustomMode);
        Assert.True(page.IsCategorySelectionEnabled);
        Assert.Contains("3", page.EstimateText);

        page.Categories[1].IsSelected = false;

        Assert.Equal("共 2 个文件", page.SelectedFilesText);
        Assert.Contains("2", page.EstimateText);
        Assert.True(page.StartExportCommand.CanExecute(null));

        page.Categories[0].IsSelected = false;
        Assert.False(page.StartExportCommand.CanExecute(null));
        Assert.False(page.HasSelection);
    }

    [Fact]
    public async Task DiagnosticsModeHidesCategoriesAndUsesDiagnosticsNote()
    {
        var service = new FakeExportService(Plan());
        var page = Create(service);
        await page.InitializeAsync();

        page.UseDiagnosticsModeCommand.Execute(null);

        Assert.True(page.IsDiagnosticsMode);
        Assert.False(page.ShowCategories);
        Assert.Equal("预计大小：未知", page.EstimateText);
        Assert.Contains("诊断包", page.DiagnosticsNote);
        Assert.Contains("排除", page.DiagnosticsNote);
        Assert.True(page.StartExportCommand.CanExecute(null));
    }

    [Fact]
    public async Task FullExportConfirmsAndAppliesTheRealReport()
    {
        var report = Report(complete: true, skipped: []);
        var service = new FakeExportService(Plan(), report);
        var dialogs = new FakeDialogService();
        string? opened = null;
        var page = Create(service, dialogs, path => opened = path);
        await page.InitializeAsync();

        await page.StartExportCommand.ExecuteAsync(null);

        Assert.Equal(1, service.ExportCalls);
        Assert.Equal(InstanceExportMode.Full, service.LastRequest!.Mode);
        Assert.NotNull(dialogs.LastMessage);
        Assert.Contains("完整导出", dialogs.LastMessage);
        Assert.True(page.IsCompleted);
        Assert.True(page.HasResult);
        Assert.True(page.ResultIsComplete);
        Assert.False(page.IsPartialResult);
        Assert.Contains(report.OutputPath, page.ResultPath);
        Assert.Contains("1 MB", page.ResultSize);
        Assert.Empty(page.Skipped);
        Assert.True(page.OpenFolderCommand.CanExecute(null));

        page.OpenFolderCommand.Execute(null);
        Assert.Equal(report.OutputDirectory, opened);
    }

    [Fact]
    public async Task PartialExportReportsSkippedFilesInsteadOfSuccess()
    {
        var report = Report(
            complete: false,
            skipped: [new ExportSkippedFile("mods/locked.jar", ExportSkipReason.Unreadable, "被占用")]);
        var service = new FakeExportService(Plan(), report);
        var page = Create(service);
        await page.InitializeAsync();

        await page.StartExportCommand.ExecuteAsync(null);

        Assert.True(page.IsCompleted);
        Assert.False(page.ResultIsComplete);
        Assert.True(page.IsPartialResult);
        Assert.Single(page.Skipped);
        Assert.Equal("mods/locked.jar", page.Skipped[0].RelativePath);
        Assert.Contains("跳过", page.ResultSummary);
    }

    [Fact]
    public async Task CustomExportSendsOnlyTheSelectedCategories()
    {
        var report = Report(complete: true, skipped: []);
        var service = new FakeExportService(Plan(), report);
        var page = Create(service);
        await page.InitializeAsync();
        page.UseCustomModeCommand.Execute(null);
        page.Categories[0].IsSelected = false;

        await page.StartExportCommand.ExecuteAsync(null);

        Assert.Equal(InstanceExportMode.Custom, service.LastRequest!.Mode);
        Assert.NotNull(service.LastRequest.Categories);
        Assert.Equal(["worlds"], service.LastRequest.Categories);
    }

    [Fact]
    public async Task DiagnosticsExportUsesTheDiagnosticsRequestAndReport()
    {
        var report = new InstanceExportReport(
            Path.Combine("exports", "diag.zip"),
            "exports",
            2048,
            FileCount: 0,
            ExcludedFileCount: 0,
            Skipped: [],
            IsComplete: true,
            IsDiagnostics: true,
            DiagnosticEntries: 9,
            ManifestEntry: null);
        var service = new FakeExportService(Plan(), report);
        var page = Create(service);
        await page.InitializeAsync();
        page.UseDiagnosticsModeCommand.Execute(null);

        await page.StartExportCommand.ExecuteAsync(null);

        Assert.Equal(InstanceExportMode.Diagnostics, service.LastRequest!.Mode);
        Assert.True(page.IsCompleted);
        Assert.Contains("诊断条目", page.ResultFiles);
    }

    [Fact]
    public async Task ChangingModeAfterExportClearsTheStaleResult()
    {
        var service = new FakeExportService(Plan(), Report(complete: true, skipped: []));
        var page = Create(service);
        await page.InitializeAsync();
        await page.StartExportCommand.ExecuteAsync(null);
        Assert.True(page.HasResult);
        Assert.True(page.IsCompleted);

        page.UseCustomModeCommand.Execute(null);

        Assert.False(page.HasResult);
        Assert.Equal(ExportUiState.Idle, page.State);
        Assert.False(page.IsCompleted);
    }

    [Fact]
    public async Task ConfirmCancelDoesNotStartTheExport()
    {
        var service = new FakeExportService(Plan());
        var dialogs = new FakeDialogService { ConfirmResult = false };
        var page = Create(service, dialogs);
        await page.InitializeAsync();

        await page.StartExportCommand.ExecuteAsync(null);

        Assert.Equal(0, service.ExportCalls);
        Assert.False(page.HasResult);
    }

    [Fact]
    public async Task CancelledExportIsShownAsCancelledNotCompleted()
    {
        var service = new FakeExportService(Plan()) { Cancel = true };
        var page = Create(service);
        await page.InitializeAsync();

        await page.StartExportCommand.ExecuteAsync(null);

        Assert.True(page.IsCancelled);
        Assert.False(page.IsCompleted);
        Assert.False(page.HasResult);
        Assert.Contains("取消", page.StatusMessage);
    }

    [Fact]
    public async Task FailedExportShowsErrorAndNoResult()
    {
        var service = new FakeExportService(Plan()) { Fail = true };
        var page = Create(service);
        await page.InitializeAsync();

        await page.StartExportCommand.ExecuteAsync(null);

        Assert.True(page.IsFailed);
        Assert.False(page.HasResult);
        Assert.Contains("失败", page.ErrorMessage);
    }

    [Fact]
    public async Task PlanFailureDisablesExportAndShowsError()
    {
        var service = new FakeExportService(Plan()) { PlanFails = true };
        var page = Create(service);

        await page.InitializeAsync();

        Assert.False(page.StartExportCommand.CanExecute(null));
        Assert.True(page.IsFailed);
        Assert.Contains("失败", page.ErrorMessage);
        Assert.Empty(page.Categories);
    }

    private static InstanceExportViewModel Create(
        FakeExportService service,
        FakeDialogService? dialogs = null,
        Action<string>? openFolder = null) =>
        new(
            "instance-1",
            service,
            dialogs ?? new FakeDialogService(),
            Localizer(),
            openFolder);

    private static InstanceExportPlan Plan() =>
        new(
            [
                new ExportCategorySummary("mods", 2, 2048, 0, true),
                new ExportCategorySummary("worlds", 1, 1024, 0, true),
            ],
            3,
            3072,
            0,
            true);

    private static InstanceExportReport Report(bool complete, IReadOnlyList<ExportSkippedFile> skipped) =>
        new(
            Path.Combine("C:", "instance", "exports", "ecl-export-test.zip"),
            Path.Combine("C:", "instance", "exports"),
            1024 * 1024,
            3,
            0,
            skipped,
            complete,
            IsDiagnostics: false,
            DiagnosticEntries: 0,
            ManifestEntry: "manifest.json");

    private sealed class FakeExportService(InstanceExportPlan plan, InstanceExportReport? report = null)
        : IInstanceExportService
    {
        public int ExportCalls { get; private set; }

        public ExportRequest? LastRequest { get; private set; }

        public bool PlanFails { get; set; }

        public bool Cancel { get; set; }

        public bool Fail { get; set; }

        public Task<Result<InstanceExportPlan>> PlanAsync(
            string instanceId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(PlanFails
                ? Result<InstanceExportPlan>.Fail("export.plan_failed", "扫描实例内容失败。")
                : Result<InstanceExportPlan>.Ok(plan));

        public Task<Result<InstanceExportReport>> ExportAsync(
            string instanceId,
            ExportRequest request,
            IProgress<InstanceExportProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            ExportCalls++;
            LastRequest = request;
            progress?.Report(new InstanceExportProgress("export-pack", 1, 3, 1024, 3072, "mods/a.jar"));
            if (Cancel)
            {
                return Task.FromResult(Result<InstanceExportReport>.Fail("export.cancelled", "导出已取消。"));
            }

            if (Fail)
            {
                return Task.FromResult(Result<InstanceExportReport>.Fail("export.failed", "导出失败。"));
            }

            return Task.FromResult(Result<InstanceExportReport>.Ok(report!));
        }
    }

    private sealed class FakeDialogService : IDialogService
    {
        public bool ConfirmResult { get; set; } = true;

        public string? LastMessage { get; private set; }

        public bool Confirm(string title, string message, string? acknowledgementToken = null)
        {
            LastMessage = message;
            return ConfirmResult;
        }

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
