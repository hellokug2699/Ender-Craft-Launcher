// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.IO;
using System.Windows.Media;
using Ecl.App.Services;
using Ecl.App.Workspace.ViewModels;
using Ecl.Core;
using Ecl.Core.Localization;
using Ecl.Minecraft.Screenshots;
using Xunit;

namespace Ecl.App.Tests.Workspace;

/// <summary>
/// 截图页（B2c，screenshots.md）：三种展示形式共享条目与选择、时间排序、按需缩略图、
/// 页面内预览切换，以及删除确认/失败时保留真实列表。
/// </summary>
public sealed class ScreenshotPageTests
{
    private static ILocalizer Localizer() => new JsonLocalizer(CultureInfo.GetCultureInfo("zh-CN"));

    [Fact]
    public async Task LoadsNewestFirstAndReportsCounts()
    {
        var service = new FakeScreenshotService(
            Entry("oldest.png", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            Entry("middle.png", new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero)),
            Entry("newest.png", new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)));
        var page = Create(service);

        await page.InitializeAsync();

        Assert.Equal(["newest.png", "middle.png", "oldest.png"], page.Items.Select(item => item.FileName));
        Assert.Equal("共 3 张", page.CountText);
        Assert.True(page.IsListView);
        Assert.False(page.IsEmpty);
        Assert.True(page.HasItems);
    }

    [Fact]
    public async Task ViewModesKeepTheSameItemsAndSelection()
    {
        var service = new FakeScreenshotService(
            Entry("alpha.png", DateTimeOffset.UtcNow),
            Entry("beta.png", DateTimeOffset.UtcNow.AddMinutes(-1)));
        var page = Create(service);
        await page.InitializeAsync();
        var selected = page.Items[0];
        selected.IsSelected = true;

        page.UseGridModeCommand.Execute(null);
        page.UseMinimalModeCommand.Execute(null);

        Assert.True(page.IsMinimalView);
        Assert.Equal(2, page.Items.Count);
        Assert.Same(selected, page.Items[0]);
        Assert.True(page.Items[0].IsSelected);
        Assert.Equal(1, page.SelectedCount);
    }

    [Fact]
    public async Task SortCommandsReorderWithoutChangingTheSet()
    {
        var service = new FakeScreenshotService(
            Entry("oldest.png", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            Entry("newest.png", new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero)));
        var page = Create(service);
        await page.InitializeAsync();

        page.SortOldestCommand.Execute(null);
        Assert.Equal(["oldest.png", "newest.png"], page.Items.Select(item => item.FileName));
        Assert.False(page.IsNewestFirst);

        page.SortNewestCommand.Execute(null);
        Assert.Equal(["newest.png", "oldest.png"], page.Items.Select(item => item.FileName));
    }

    [Fact]
    public async Task SelectAllAndDeleteSubmitOnlyTheSelectedPaths()
    {
        var service = new FakeScreenshotService(
            Entry("alpha.png", DateTimeOffset.UtcNow),
            Entry("beta.png", DateTimeOffset.UtcNow.AddMinutes(-1)),
            Entry("gamma.png", DateTimeOffset.UtcNow.AddMinutes(-2)));
        var page = Create(service);
        await page.InitializeAsync();

        page.ToggleSelectAllCommand.Execute(null);
        Assert.Equal(3, page.SelectedCount);
        Assert.True(page.DeleteSelectedCommand.CanExecute(null));

        await page.DeleteSelectedCommand.ExecuteAsync(null);

        var batch = Assert.Single(service.DeleteBatches);
        Assert.Equal(
            ["screenshots/alpha.png", "screenshots/beta.png", "screenshots/gamma.png"],
            batch.Select(path => path.Replace('\\', '/')));
        Assert.Empty(page.Items);
        Assert.Contains("已删除 3", page.StatusMessage);
    }

    [Fact]
    public async Task DeleteCancelKeepsSelectionAndNeverCallsTheDomain()
    {
        var service = new FakeScreenshotService(Entry("alpha.png", DateTimeOffset.UtcNow));
        var dialogs = new FakeDialogService { ConfirmResult = false };
        var page = Create(service, dialogs);
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
        var service = new FakeScreenshotService(Entry("alpha.png", DateTimeOffset.UtcNow))
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
    public async Task ThumbnailIsReadOnDemandOnlyOnce()
    {
        var service = new FakeScreenshotService(Entry("alpha.png", DateTimeOffset.UtcNow));
        var images = new FakeImageLoader();
        var page = Create(service, images: images);
        await page.InitializeAsync();

        Assert.Equal(0, service.ReadCalls);
        await page.EnsureThumbnailAsync(page.Items[0]);
        await page.EnsureThumbnailAsync(page.Items[0]);

        Assert.Equal(1, service.ReadCalls);
        Assert.NotNull(page.Items[0].Thumbnail);
        Assert.True(page.Items[0].HasThumbnail);
        Assert.False(page.Items[0].ThumbnailFailed);
    }

    [Fact]
    public async Task UndecodableThumbnailShowsPlaceholderWithoutFakingImage()
    {
        var service = new FakeScreenshotService(Entry("alpha.png", DateTimeOffset.UtcNow));
        var images = new FakeImageLoader { FailDecode = true };
        var page = Create(service, images: images);
        await page.InitializeAsync();

        await page.EnsureThumbnailAsync(page.Items[0]);

        Assert.Null(page.Items[0].Thumbnail);
        Assert.False(page.Items[0].HasThumbnail);
        Assert.True(page.Items[0].ThumbnailFailed);
    }

    [Fact]
    public async Task UnrecognizedImageIsNotReadOrPreviewedAsAnImage()
    {
        var service = new FakeScreenshotService(Entry("broken.png", DateTimeOffset.UtcNow, recognized: false));
        var page = Create(service);
        await page.InitializeAsync();

        await page.EnsureThumbnailAsync(page.Items[0]);

        Assert.Equal(0, service.ReadCalls);
        Assert.False(page.Items[0].IsRecognizedImage);
    }

    [Fact]
    public async Task PreviewOpensInPageNavigatesAndCloses()
    {
        var service = new FakeScreenshotService(
            Entry("alpha.png", DateTimeOffset.UtcNow),
            Entry("beta.png", DateTimeOffset.UtcNow.AddMinutes(-1)),
            Entry("gamma.png", DateTimeOffset.UtcNow.AddMinutes(-2)));
        var page = Create(service);
        await page.InitializeAsync();

        await page.OpenPreviewCommand.ExecuteAsync(page.Items[1]);

        Assert.True(page.IsPreviewOpen);
        Assert.Equal("beta.png", page.PreviewItem!.FileName);
        Assert.Equal("第 2 / 3 张", page.PreviewOfText);
        Assert.True(page.CanGoPrevious);
        Assert.True(page.CanGoNext);
        Assert.NotNull(page.PreviewImage);

        await page.NextPreviewCommand.ExecuteAsync(null);
        Assert.Equal("gamma.png", page.PreviewItem!.FileName);
        Assert.False(page.CanGoNext);

        await page.PreviousPreviewCommand.ExecuteAsync(null);
        await page.PreviousPreviewCommand.ExecuteAsync(null);
        Assert.Equal("alpha.png", page.PreviewItem!.FileName);
        Assert.False(page.CanGoPrevious);

        page.ClosePreviewCommand.Execute(null);
        Assert.False(page.IsPreviewOpen);
        Assert.Null(page.PreviewItem);
        Assert.Null(page.PreviewImage);
    }

    [Fact]
    public async Task ListFailureShowsErrorInsteadOfEmptyState()
    {
        var service = new FakeScreenshotService(Entry("alpha.png", DateTimeOffset.UtcNow))
        {
            ListFails = true,
        };
        var page = Create(service);

        await page.InitializeAsync();

        Assert.Empty(page.Items);
        Assert.NotEmpty(page.ErrorMessage);
        Assert.False(page.IsEmpty);
    }

    [Fact]
    public async Task DisposeStopsFurtherThumbnailReads()
    {
        var service = new FakeScreenshotService(Entry("alpha.png", DateTimeOffset.UtcNow));
        var page = Create(service);
        await page.InitializeAsync();
        var item = page.Items[0];

        page.Dispose();
        await page.EnsureThumbnailAsync(item);

        Assert.True(page.IsDisposed);
        Assert.Empty(page.Items);
        Assert.Equal(0, service.ReadCalls);
    }

    private static ScreenshotPageViewModel Create(
        FakeScreenshotService service,
        FakeDialogService? dialogs = null,
        FakeImageLoader? images = null) =>
        new(
            "instance-1",
            service,
            images ?? new FakeImageLoader(),
            dialogs ?? new FakeDialogService(),
            Localizer());

    private static ScreenshotEntry Entry(string name, DateTimeOffset modified, bool recognized = true) =>
        new(name, Path.Combine("screenshots", name), 1024, modified, recognized);

    private sealed class FakeScreenshotService(params ScreenshotEntry[] entries) : IScreenshotService
    {
        private readonly List<ScreenshotEntry> _entries = [.. entries];

        public List<List<string>> DeleteBatches { get; } = [];

        public int ReadCalls { get; private set; }

        public bool ListFails { get; set; }

        public bool DeleteFails { get; set; }

        public Task<Result<ScreenshotList>> ListAsync(
            string instanceId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ListFails
                ? Result<ScreenshotList>.Fail("screenshots.list_failed", "读取截图目录失败。")
                : Result<ScreenshotList>.Ok(new ScreenshotList([.. _entries], 0)));

        public Task<Result<byte[]>> ReadImageAsync(
            string instanceId,
            string relativePath,
            int maxBytes = ScreenshotService.MaxReadBytes,
            CancellationToken cancellationToken = default)
        {
            ReadCalls++;
            return Task.FromResult(Result<byte[]>.Ok([1, 2, 3, 4]));
        }

        public Task<Result<ScreenshotDeleteReport>> DeleteAsync(
            string instanceId,
            IReadOnlyList<string> relativePaths,
            CancellationToken cancellationToken = default)
        {
            DeleteBatches.Add([.. relativePaths]);
            if (DeleteFails)
            {
                return Task.FromResult(Result<ScreenshotDeleteReport>.Fail(
                    "screenshots.delete_failed",
                    "删除截图失败。"));
            }

            var deleted = new HashSet<string>(relativePaths, StringComparer.Ordinal);
            _entries.RemoveAll(entry => deleted.Contains(entry.RelativePath));
            return Task.FromResult(Result<ScreenshotDeleteReport>.Ok(
                new ScreenshotDeleteReport([.. relativePaths], RecoveryRequired: false)));
        }
    }

    private sealed class FakeImageLoader : IScreenshotImageLoader
    {
        public bool FailDecode { get; set; }

        public ImageSource? Load(byte[] bytes, int decodePixelWidth) =>
            FailDecode ? null : new DrawingImage();
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
