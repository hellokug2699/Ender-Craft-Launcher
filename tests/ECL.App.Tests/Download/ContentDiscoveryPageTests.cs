// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.IO;
using Ecl.App.Dialogs;
using Ecl.App.Dialogs.ViewModels;
using Ecl.App.Services;
using Ecl.App.ViewModels;
using Ecl.Configuration;
using Ecl.Configuration.Settings;
using Ecl.Core;
using Ecl.Core.Localization;
using Ecl.Minecraft.Content;
using Ecl.Minecraft.Instances;
using Ecl.Minecraft.Mods;
using Ecl.Minecraft.Versions;
using Xunit;

namespace Ecl.App.Tests.Download;

/// <summary>
/// 下载中心内容发现页（B4a 步骤 2）：搜索英文限制、来源切换、已有实例/自定义筛选、
/// 版本详情两表、目标确认与替换目标、条级进度与关闭弹窗后的状态保持、
/// 整合包与数据包的能力边界。
/// </summary>
public sealed class ContentDiscoveryPageTests
{
    private static ILocalizer Localizer() => new JsonLocalizer(CultureInfo.GetCultureInfo("zh-CN"));

    [Fact]
    public async Task ExistingInstanceModeLocksVersionAndLoaderAndSearchesWithThem()
    {
        using var harness = new Harness();
        var page = harness.Create(ContentKind.Mod);
        await page.EnsureLoadedAsync();

        page.UseExistingInstance = true;
        page.SelectedInstance = page.TargetInstances[0];
        await page.SearchCommand.ExecuteAsync(null);

        Assert.NotNull(harness.Search.LastQuery);
        Assert.Equal("1.20.1", harness.Search.LastQuery!.GameVersion);
        Assert.Equal("fabric", harness.Search.LastQuery.Loader);
        Assert.Contains("已锁定", page.InstanceHint);
        Assert.False(page.ShowsCustomFilters);
        Assert.True(page.ShowsInstanceSelector);
    }

    [Fact]
    public async Task CustomModeFiltersByTheSelectedVersionAndLoader()
    {
        using var harness = new Harness();
        var page = harness.Create(ContentKind.Mod);
        await page.EnsureLoadedAsync();

        Assert.False(page.UseExistingInstance);
        Assert.True(page.ShowsCustomFilters);
        page.SelectedGameVersion = page.GameVersions.First(choice => choice.Value == "1.21.1");
        page.SelectedLoader = page.LoaderChoices.First(choice => choice.Id == "forge");
        await page.SearchCommand.ExecuteAsync(null);

        Assert.Equal("1.21.1", harness.Search.LastQuery!.GameVersion);
        Assert.Equal("forge", harness.Search.LastQuery.Loader);
    }

    [Fact]
    public async Task NonAsciiSearchInputIsMarkedInvalidAndNeverSubmitted()
    {
        using var harness = new Harness();
        var page = harness.Create(ContentKind.Mod);
        await page.EnsureLoadedAsync();

        page.Query = "中文搜索";

        Assert.True(page.IsSearchInputInvalid);
        await page.SearchCommand.ExecuteAsync(null);
        Assert.Equal(0, harness.Search.Calls);
        Assert.Equal("搜索只支持英文输入；中文输入不会被提交。", page.StatusMessage);

        page.Query = "sodium";
        Assert.False(page.IsSearchInputInvalid);
        await page.SearchCommand.ExecuteAsync(null);
        Assert.Equal(1, harness.Search.Calls);
    }

    [Fact]
    public async Task SourceSwitchSearchesOnlyThatSource()
    {
        using var harness = new Harness();
        var page = harness.Create(ContentKind.Mod);
        await page.EnsureLoadedAsync();

        page.SelectedSource = page.Sources.Single(source => source.Filter == ContentSourceFilter.Modrinth);
        await page.SearchCommand.ExecuteAsync(null);

        Assert.Equal(ContentSourceFilter.Modrinth, harness.Search.LastQuery!.Source);
    }

    [Fact]
    public async Task ResultRowShowsOnlyFieldsProvidedByTheSource()
    {
        using var harness = new Harness();
        var page = harness.Create(ContentKind.Mod);
        await page.EnsureLoadedAsync();
        await page.SearchCommand.ExecuteAsync(null);

        var row = page.Results.Single(result => result.Hit.Source == ContentSource.Modrinth);
        Assert.Equal("1.20.1 – 1.20.2", row.VersionRangeText);
        Assert.Equal("Fabric", row.LoaderText);
        Assert.Equal("Modrinth", row.SourceLabel);
        Assert.Equal("大小未知", row.SizeText);
        Assert.Equal("Demo Mod", row.Name);
    }

    [Fact]
    public async Task ResultRowShowsTheEnrichedFileSizeWhenAvailable()
    {
        using var harness = new Harness();
        harness.Search.Hits[0] = harness.Search.Hits[0] with { FileSize = 4096 };
        var page = harness.Create(ContentKind.Mod);
        await page.EnsureLoadedAsync();
        await page.SearchCommand.ExecuteAsync(null);

        Assert.Equal("4 KB", page.Results[0].SizeText);
    }

    [Fact]
    public async Task DetailDialogSplitsFilteredAndAllVersionsAndAllowsDuplicates()
    {
        using var harness = new Harness();
        var page = harness.Create(ContentKind.Mod);
        await page.EnsureLoadedAsync();
        page.UseExistingInstance = true;
        page.SelectedInstance = page.TargetInstances[0];
        await page.SearchCommand.ExecuteAsync(null);

        await page.OpenDetailCommand.ExecuteAsync(page.Results[0]);
        var detail = harness.Dialogs.LastContent<ContentDetailDialogViewModel>();
        await detail.LoadAsync();

        Assert.True(detail.HasFilter);
        Assert.Equal("作者：author", detail.AuthorText);
        Assert.Equal("1.20.1+Fabric 类", detail.FilterTitle);
        Assert.Equal(2, detail.AllVersions.Count);
        var filtered = Assert.Single(detail.FilteredVersions);
        Assert.Equal("v1", filtered.Version.VersionId);
        Assert.Equal("2 KB", detail.AllVersions[0].SizeText);
        Assert.Equal("大小未知", detail.AllVersions[1].SizeText);
    }

    [Fact]
    public async Task ConfirmedDownloadInstallsTheChosenVersionAndKeepsRowStateAfterReopening()
    {
        using var harness = new Harness();
        var page = harness.Create(ContentKind.Mod);
        await page.EnsureLoadedAsync();
        page.UseExistingInstance = true;
        page.SelectedInstance = page.TargetInstances[0];
        await page.SearchCommand.ExecuteAsync(null);

        await page.OpenDetailCommand.ExecuteAsync(page.Results[0]);
        var detail = harness.Dialogs.LastContent<ContentDetailDialogViewModel>();
        await detail.LoadAsync();
        var row = detail.FilteredVersions[0];

        harness.Dialogs.Fallback = new DialogResult(DialogOutcome.Primary);
        harness.Install.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await detail.DownloadVersionCommand.ExecuteAsync(row);
        await WaitUntilAsync(() => row.IsActive);

        var confirm = Assert.IsType<MessageDialogViewModel>(harness.Dialogs.Requests[^1].Content);
        Assert.Contains("Alpha", confirm.Message);
        Assert.Equal("即将安装到实例「Alpha」，是否确定？", confirm.Message);
        Assert.Equal(ContentVersionStatus.Downloading, row.Status);

        harness.Install.Gate.SetResult();
        await page.WhenDownloadsSettledAsync();

        var call = Assert.Single(harness.Install.InstalledVersions);
        Assert.Equal("alpha", call.InstanceId);
        Assert.Equal("v1", call.Version.VersionId);
        Assert.Equal(ContentVersionStatus.Completed, row.Status);

        // 关闭弹窗不取消任务；重新打开同一项目仍显示同一行的真实状态。
        await page.OpenDetailCommand.ExecuteAsync(page.Results[0]);
        var reopened = harness.Dialogs.LastContent<ContentDetailDialogViewModel>();
        await reopened.LoadAsync();
        var reopenedRow = reopened.AllVersions.Single(version => version.Version.VersionId == "v1");
        Assert.Same(row, reopenedRow);
        Assert.Equal(ContentVersionStatus.Completed, reopenedRow.Status);
    }

    [Fact]
    public async Task CancelledConfirmationStartsNothing()
    {
        using var harness = new Harness();
        var page = harness.Create(ContentKind.Mod);
        await page.EnsureLoadedAsync();
        page.UseExistingInstance = true;
        page.SelectedInstance = page.TargetInstances[0];
        await page.SearchCommand.ExecuteAsync(null);

        await page.OpenDetailCommand.ExecuteAsync(page.Results[0]);
        var detail = harness.Dialogs.LastContent<ContentDetailDialogViewModel>();
        await detail.LoadAsync();

        harness.Dialogs.Fallback = DialogResult.Cancel;
        await detail.DownloadVersionCommand.ExecuteAsync(detail.FilteredVersions[0]);

        Assert.Empty(harness.Install.InstalledVersions);
        Assert.Equal(ContentVersionStatus.Idle, detail.FilteredVersions[0].Status);
    }

    [Fact]
    public async Task InstallToAnotherLocationUsesTheChosenInstance()
    {
        using var harness = new Harness();
        var page = harness.Create(ContentKind.Mod);
        await page.EnsureLoadedAsync();
        page.UseExistingInstance = true;
        page.SelectedInstance = page.TargetInstances[0];
        await page.SearchCommand.ExecuteAsync(null);
        await page.OpenDetailCommand.ExecuteAsync(page.Results[0]);
        var detail = harness.Dialogs.LastContent<ContentDetailDialogViewModel>();
        await detail.LoadAsync();

        harness.Dialogs.Results.Enqueue(new DialogResult(DialogOutcome.Secondary));
        harness.Dialogs.Results.Enqueue(new DialogResult(
            DialogOutcome.Primary,
            new ContentTargetChoice("beta", "Beta", null)));
        await detail.DownloadVersionCommand.ExecuteAsync(detail.FilteredVersions[0]);
        await page.WhenDownloadsSettledAsync();

        var call = Assert.Single(harness.Install.InstalledVersions);
        Assert.Equal("beta", call.InstanceId);
    }

    [Fact]
    public async Task CustomModePicksAFolderAndDownloadsThereAfterConfirmation()
    {
        using var harness = new Harness();
        var page = harness.Create(ContentKind.Mod);
        await page.EnsureLoadedAsync();
        await page.SearchCommand.ExecuteAsync(null);
        await page.OpenDetailCommand.ExecuteAsync(page.Results[0]);
        var detail = harness.Dialogs.LastContent<ContentDetailDialogViewModel>();
        await detail.LoadAsync();

        var target = Directory.CreateTempSubdirectory("ecl-download-target-").FullName;
        try
        {
            harness.FileDialogs.PickFolderResult = target;
            harness.Dialogs.Fallback = new DialogResult(DialogOutcome.Primary);
            await detail.DownloadVersionCommand.ExecuteAsync(detail.AllVersions[0]);
            await page.WhenDownloadsSettledAsync();

            var call = Assert.Single(harness.Install.DownloadedVersions);
            Assert.Equal(target, call.Directory);
            Assert.Equal("v2", call.Version.VersionId);
            var confirm = Assert.IsType<MessageDialogViewModel>(harness.Dialogs.Requests[^1].Content);
            Assert.Contains(target, confirm.Message);
        }
        finally
        {
            Directory.Delete(target, recursive: true);
        }
    }

    [Fact]
    public async Task ResourcePacksRequireARegisteredInstanceAndDoNotFilterByLoader()
    {
        using var harness = new Harness();
        var page = harness.Create(ContentKind.ResourcePack);
        await page.EnsureLoadedAsync();

        Assert.False(page.ShowsTargetModeCheckBox);
        Assert.True(page.IsTargetLocked);
        Assert.True(page.HasInstances);
        await page.SearchCommand.ExecuteAsync(null);

        Assert.Equal("1.20.1", harness.Search.LastQuery!.GameVersion);
        Assert.Null(harness.Search.LastQuery.Loader);
    }

    [Fact]
    public async Task MissingTargetInstanceBlocksTheSearchInsteadOfFallingBackToCustomFilters()
    {
        using var harness = new Harness();
        var noInstances = harness.Create(ContentKind.ShaderPack, withInstances: false);
        await noInstances.EnsureLoadedAsync();

        Assert.True(noInstances.ShowNoInstances);
        await noInstances.SearchCommand.ExecuteAsync(null);
        Assert.Equal(0, harness.Search.Calls);
        Assert.Equal("请先选择目标实例。", noInstances.StatusMessage);
    }

    [Fact]
    public async Task ActivatingWithAMissingInstanceReportsItAndClearsResults()
    {
        using var harness = new Harness();
        var page = harness.Create(ContentKind.Mod);
        await page.ActivateAsync(ContentSearchContext.ForExistingInstance(
            ContentKind.Mod,
            "removed-instance",
            gameVersion: null,
            loader: null,
            query: "sodium"));

        Assert.Equal("目标实例已失效，请重新选择实例后再安装模组。", page.StatusMessage);
        Assert.Empty(page.Results);
    }

    [Fact]
    public async Task ActivatingFromTheWorkspaceLocksTheContextAndSearches()
    {
        using var harness = new Harness();
        var page = harness.Create(ContentKind.Mod);
        await page.ActivateAsync(ContentSearchContext.ForExistingInstance(
            ContentKind.Mod,
            "alpha",
            gameVersion: "1.20.1",
            loader: "fabric",
            query: "sodium"));

        Assert.True(page.UseExistingInstance);
        Assert.Equal("alpha", page.SelectedInstance?.Id);
        Assert.Equal("sodium", page.Query);
        Assert.Equal("1.20.1", harness.Search.LastQuery!.GameVersion);
        Assert.Equal(2, page.Results.Count);
    }

    [Fact]
    public async Task ModpackVersionsEnterTheSharedModpackDeploymentInsteadOfInstallingHere()
    {
        using var harness = new Harness();
        var page = harness.Create(ContentKind.ModPack);
        ContentHit? openedHit = null;
        ContentVersion? openedVersion = null;
        page.OpenModpackDeployment = (hit, version) =>
        {
            openedHit = hit;
            openedVersion = version;
            return Task.CompletedTask;
        };

        await page.EnsureLoadedAsync();
        await page.SearchCommand.ExecuteAsync(null);
        await page.OpenDetailCommand.ExecuteAsync(page.Results[0]);
        var detail = harness.Dialogs.LastContent<ContentDetailDialogViewModel>();
        await detail.LoadAsync();

        var row = detail.AllVersions[0];
        Assert.True(row.CanRequestDownload);
        Assert.True(row.CanDownload);

        harness.Dialogs.Fallback = new DialogResult(DialogOutcome.Primary);
        await detail.DownloadVersionCommand.ExecuteAsync(row);

        Assert.Same(row.Hit, openedHit);
        Assert.Same(row.Version, openedVersion);
        Assert.Empty(harness.Install.InstalledVersions);
        Assert.Empty(harness.Install.DownloadedVersions);
        Assert.Equal(ContentVersionStatus.Idle, row.Status);
    }

    [Fact]
    public async Task DatapacksShowTheirBlockedStateWithoutSearching()
    {
        using var harness = new Harness();
        var page = harness.Create(ContentKind.DataPack);
        await page.EnsureLoadedAsync();

        Assert.True(page.IsUnavailable);
        Assert.Contains("世界", page.UnavailableReason);
        await page.SearchCommand.ExecuteAsync(null);
        Assert.Equal(0, harness.Search.Calls);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("条件在预期时间内没有满足。");
    }

    private sealed class Harness : IDisposable
    {
        private readonly SynchronizationContext? _previous;

        public Harness()
        {
            // 让 Progress<T> 同步回调：条级进度在测试中可确定地观察。
            _previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new ImmediateSynchronizationContext());
            Instances.Existing.AddRange(
            [
                new GameInstance
                {
                    Id = "alpha",
                    Name = "Alpha",
                    GameVersion = "1.20.1",
                    VersionId = "1.20.1",
                    Loader = "fabric",
                    LoaderVersion = "0.16.0",
                },
                new GameInstance
                {
                    Id = "beta",
                    Name = "Beta",
                    GameVersion = "1.21.1",
                    VersionId = "1.21.1",
                    Loader = "forge",
                    LoaderVersion = "47.2.0",
                },
            ]);
            Versions.Versions.AddRange(
            [
                new MinecraftGameVersion("1.21.1", MinecraftVersionKind.Release, new DateTimeOffset(2024, 8, 8, 0, 0, 0, TimeSpan.Zero)),
                new MinecraftGameVersion("1.20.1", MinecraftVersionKind.Release, new DateTimeOffset(2023, 6, 12, 0, 0, 0, TimeSpan.Zero)),
            ]);
            Search.Hits.AddRange(
            [
                new ContentHit(
                    ContentSource.Modrinth,
                    "alpha",
                    "alpha",
                    "Demo Mod",
                    "desc",
                    "author",
                    10,
                    null,
                    "https://modrinth.com/mod/alpha",
                    null,
                    ContentKind.Mod)
                {
                    GameVersions = ["1.20.1", "1.20.2"],
                    Loaders = ["fabric"],
                },
                new ContentHit(
                    ContentSource.CurseForge,
                    "42",
                    "cf-mod",
                    "CF Demo",
                    "desc",
                    null,
                    5,
                    null,
                    "https://www.curseforge.com/minecraft/mc-mods/cf-demo",
                    null,
                    ContentKind.Mod)
                {
                    GameVersions = ["1.20.1"],
                    Loaders = ["forge"],
                },
            ]);
            Catalog.Versions.Add(new ContentVersion(
                ContentSource.Modrinth,
                "alpha",
                "v2",
                "2.0",
                ["1.21.1"],
                ["fabric"],
                "demo-2.0.jar",
                2048,
                new DateTimeOffset(2024, 8, 1, 0, 0, 0, TimeSpan.Zero)));
            Catalog.Versions.Add(new ContentVersion(
                ContentSource.Modrinth,
                "alpha",
                "v1",
                "1.0",
                ["1.20.1"],
                ["fabric"],
                "demo-1.0.jar",
                null,
                new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        }

        public FakeSearchService Search { get; } = new();

        public FakeCatalog Catalog { get; } = new();

        public FakeInstallService Install { get; } = new();

        public FakeInstanceService Instances { get; } = new();

        public FakeVersionCatalog Versions { get; } = new();

        public FakeSettingsService Settings { get; } = new();

        public FakeDialogEngine Dialogs { get; } = new();

        public FakeDialogService FileDialogs { get; } = new();

        public FakeShaderSupportService Shaders { get; } = new();

        public ContentDiscoveryViewModel Create(ContentKind kind, bool withInstances = true)
        {
            if (!withInstances)
            {
                Instances.Existing.Clear();
            }

            var factory = new ContentDiscoveryPageFactory(
                Search,
                Catalog,
                Install,
                Instances,
                Versions,
                Settings,
                Dialogs,
                FileDialogs,
                Localizer(),
                Shaders);
            return factory.Create(kind);
        }

        public void Dispose() => SynchronizationContext.SetSynchronizationContext(_previous);
    }

    private sealed class ImmediateSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => d(state);
    }

    private sealed class FakeSearchService : IContentSearchService
    {
        public List<ContentHit> Hits { get; } = [];

        public int Calls { get; private set; }

        public ContentSearchQuery? LastQuery { get; private set; }

        public Task<Result<ContentSearchResult>> SearchAsync(
            ContentSearchQuery query,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            LastQuery = query;
            var hits = Hits.Where(hit => hit.Kind == query.Kind).ToArray();
            if (hits.Length == 0)
            {
                hits = Hits.Select(hit => hit with { Kind = query.Kind }).ToArray();
            }
            return Task.FromResult(Result<ContentSearchResult>.Ok(new ContentSearchResult(
                hits,
                hits.Length,
                new Dictionary<ContentSource, int> { [ContentSource.Modrinth] = hits.Length },
                [])));
        }
    }

    private sealed class FakeCatalog : IContentVersionCatalog
    {
        public List<ContentVersion> Versions { get; } = [];

        public Task<Result<ContentVersionCatalogResult>> GetVersionsAsync(
            ContentHit hit,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<ContentVersionCatalogResult>.Ok(
                new ContentVersionCatalogResult(Versions.ToArray(), true, [])));
    }

    private sealed class FakeInstallService : IContentInstallService
    {
        public List<(string InstanceId, ContentVersion Version)> InstalledVersions { get; } = [];

        public List<(string Directory, ContentVersion Version)> DownloadedVersions { get; } = [];

        public TaskCompletionSource? Gate { get; set; }

        public async Task<Result<ContentInstallReport>> InstallVersionAsync(
            string instanceId,
            ContentHit hit,
            ContentVersion version,
            IProgress<ContentProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            InstalledVersions.Add((instanceId, version));
            progress?.Report(new ContentProgress("download", "正在下载", 1, 1)
            {
                CompletedBytes = 1024,
                TotalBytes = 1024,
            });
            if (Gate is { } gate)
            {
                await gate.Task.ConfigureAwait(true);
            }

            return Result<ContentInstallReport>.Ok(new ContentInstallReport(
                hit.Name,
                hit.Source,
                hit.Kind,
                instanceId,
                version.FileName,
                [version.FileName],
                []));
        }

        public async Task<Result<ContentDownloadReport>> DownloadVersionAsync(
            ContentHit hit,
            ContentVersion version,
            string directory,
            IProgress<ContentProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            DownloadedVersions.Add((directory, version));
            progress?.Report(new ContentProgress("download", "正在下载", 1, 1)
            {
                CompletedBytes = 1024,
                TotalBytes = 1024,
            });
            if (Gate is { } gate)
            {
                await gate.Task.ConfigureAwait(true);
            }

            return Result<ContentDownloadReport>.Ok(new ContentDownloadReport(
                hit.Name,
                hit.Kind,
                System.IO.Path.Combine(directory, version.FileName),
                version.FileSize ?? 0));
        }

        public Task<Result<ContentInstallReport>> InstallAsync(
            string instanceId,
            ContentHit hit,
            IProgress<ContentProgress>? progress = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Result<ContentInstallReport>> InstallResolvedAsync(
            string instanceId,
            Ecl.Minecraft.Content.ResolvedDependency dependency,
            IProgress<ContentProgress>? progress = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Result<ContentDownloadReport>> DownloadAsync(
            ContentHit hit,
            IProgress<ContentProgress>? progress = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Result<ModpackImportResult>> ImportModpackAsync(
            string instanceId,
            ContentHit hit,
            IProgress<ContentProgress>? progress = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Result<ModpackImportResult>> CreateInstanceAndImportModpackAsync(
            ContentHit hit,
            IProgress<ContentProgress>? progress = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Result<ModpackPreparation>> PrepareModpackAsync(
            ContentHit hit,
            ContentVersion version,
            IProgress<ContentProgress>? progress = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Result<ModpackImportResult>> DeployModpackAsync(
            ModpackDeploymentRequest request,
            IProgress<ContentProgress>? progress = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeVersionCatalog : IVersionCatalog
    {
        public List<MinecraftGameVersion> Versions { get; } = [];

        public Task<Result<IReadOnlyList<MinecraftGameVersion>>> GetVersionsAsync(
            bool refresh = false,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<IReadOnlyList<MinecraftGameVersion>>.Ok(Versions.ToArray()));
    }

    private sealed class FakeSettingsService : ISettingsService
    {
        public IReadOnlyList<OptionDefinition> GetDefinitions(OptionScope? scope = null) => [];

        public OptionDefinition GetDefinition(string key) => throw new NotSupportedException();

        public T? Get<T>(string key, string? instanceId = null) =>
            key == "network.maxConcurrentDownloads" ? (T)(object)8 : default;

        public string GetRawString(string key, string? instanceId = null) => string.Empty;

        public SettingsValueSource GetSource(string key, string? instanceId = null) => SettingsValueSource.Default;

        public bool IsVisible(OptionDefinition definition, string? instanceId = null) => true;

        public bool TrySetRaw(string key, string? rawValue, string? instanceId, out string? error)
        {
            error = null;
            return false;
        }

        public bool TryClear(string key, string? instanceId, out string? error)
        {
            error = null;
            return false;
        }

        public event EventHandler<SettingsChangedEventArgs>? Changed
        {
            add { }
            remove { }
        }
    }

    private sealed class FakeDialogEngine : IDialogEngine
    {
        public bool IsAvailable { get; set; } = true;

        public List<DialogRequest> Requests { get; } = [];

        public Queue<DialogResult> Results { get; } = new();

        public DialogResult Fallback { get; set; } = DialogResult.Cancel;

        public DialogResult Show(DialogRequest request) => Fallback;

        public Task<DialogResult> ShowAsync(
            DialogRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(Results.Count > 0 ? Results.Dequeue() : Fallback);
        }

        public bool CloseTop(DialogOutcome outcome) => false;

        public T LastContent<T>()
            where T : class =>
            Assert.IsType<T>(Requests[^1].Content);
    }

    private sealed class FakeDialogService : IDialogService
    {
        public string? PickFolderResult { get; set; }

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

        public string? PickFolder(string title) => PickFolderResult;

        public bool OpenUrl(string url) => true;
    }

    private sealed class FakeShaderSupportService : IShaderSupportService
    {
        public Task<Result<ShaderSupportResult>> DetectAsync(
            string instanceId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<ShaderSupportResult>.Ok(new ShaderSupportResult(
                ShaderSupportState.Missing,
                [])));
    }

    private sealed class FakeInstanceService : IInstanceService
    {
        public List<GameInstance> Existing { get; } = [];

        public Task<IReadOnlyList<GameInstance>> GetInstancesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GameInstance>>(Existing.ToArray());

        public Task<GameInstance?> GetAsync(string instanceId, CancellationToken cancellationToken = default) =>
            Task.FromResult<GameInstance?>(Existing.FirstOrDefault(instance => instance.Id == instanceId));

        public Task<Result<GameInstance>> CreateAsync(
            string name,
            string gameVersion,
            string loader = "vanilla",
            string? loaderVersion = null,
            string? fabricApiVersion = null,
            string? optiFineVersion = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Result<GameInstance>> UpdateConfigurationAsync(
            string instanceId,
            string gameVersion,
            string loader,
            string? loaderVersion = null,
            string? fabricApiVersion = null,
            string? optiFineVersion = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Result<GameInstance>> RestoreConfigurationAsync(
            GameInstance restorePoint,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Result> DeleteAsync(string instanceId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<InstanceDeletionPlan>> GetDeletionPlanAsync(
            string instanceId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Result> DeleteAsync(
            string instanceId,
            InstanceDeleteOptions options,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<DetectedVersion>> ScanVersionsAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<GameInstance>> ImportAsync(
            string versionId,
            string? name = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task TouchLastPlayedAsync(string instanceId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<GameInstance>> RelocateAsync(
            string instanceId,
            string newGameDirectory,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Result<GameInstance>> CloneAsync(
            string sourceInstanceId,
            string name,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
