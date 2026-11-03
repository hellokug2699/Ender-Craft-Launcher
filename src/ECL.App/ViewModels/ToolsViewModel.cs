// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.Additions;
using Ecl.App.Services;
using Ecl.Core;
using Ecl.Deployment;
using Ecl.Diagnostics;
using Ecl.Download;
using Ecl.Minecraft.Instances;
using Ecl.Wardrobe;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ecl.App.ViewModels;

/// <summary>工具箱页：原生承载 Additions、衣柜与诊断入口；知识和原版工具展示真实可用状态。</summary>
public sealed partial class ToolsViewModel : ObservableObject
{
    public ToolsViewModel(
        AdditionsViewModel additions,
        WardrobeViewModel wardrobe,
        DiagnosticsViewModel diagnostics)
    {
        Additions = additions;
        Wardrobe = wardrobe;
        Diagnostics = diagnostics;
    }

    public AdditionsViewModel Additions { get; }

    public WardrobeViewModel Wardrobe { get; }

    public DiagnosticsViewModel Diagnostics { get; }

    public string KnowledgeStatus =>
        "受限知识浏览器策略已实现：仅允许 zh.minecraft.wiki、mcmod.cn 及其子域名的 HTTPS 页面。"
        + "WebView2 宿主尚未实现，因此当前不提供网页导航；不会用普通浏览器或代理绕过边界。";

    public string VanillaStatus =>
        "原版工具集是独立 Addition，数据包尚未安装。数据模板位于 additions/vanilla-toolkit/data-template/；"
        + "安装官方数据包后才能进行 Registry 查询等离线工具操作。";

    public string WorldAnalysisStatus =>
        "世界种子与生成规则分析（ADR 0028）仍是 Proposed，尚未实现。"
        + "当前不会根据 Seed 或版本信息伪造生成结果；请使用只读世界地图、NBT 浏览与诊断证据。";

    public string WorldEditingStatus =>
        "NBT 编辑、区块修复与诊断修复工作流（ADR 0027 / 0030）尚未完成。"
        + "当前只提供只读 NBT 树、地图与诊断标记；不会自动修改存档，也不提供未经备份的破坏性修复。";

    public async Task ActivateAsync()
    {
        await Additions.ActivateAsync().ConfigureAwait(true);
        await Wardrobe.ActivateAsync().ConfigureAwait(true);
        await Diagnostics.ActivateAsync().ConfigureAwait(true);
    }
}

public sealed partial class AdditionsViewModel : ObservableObject
{
    private static readonly ReleaseVersion CoreApiVersion = ReleaseVersion.Parse("1.0.0");
    private readonly IAppPaths _paths;
    private readonly IDialogService _dialogs;
    private readonly ILogger<AdditionsViewModel> _logger;
    private readonly AdditionStateStore _state;
    private readonly AdditionInstaller _installer;
    private readonly AdditionPlanner _planner = new();
    private AdditionCatalogSnapshot? _catalog;
    private bool _activated;

    public AdditionsViewModel(IAppPaths paths, IDownloadService download, IDialogService dialogs, ILogger<AdditionsViewModel>? logger = null)
    {
        _paths = paths;
        _dialogs = dialogs;
        _logger = logger ?? NullLogger<AdditionsViewModel>.Instance;
        var root = Path.Combine(paths.Root, "additions");
        _state = new AdditionStateStore(Path.Combine(root, "installed.json"));
        _installer = new AdditionInstaller(
            new DownloadAdditionPackageProvider(download, Path.Combine(root, "tmp")),
            _state,
            root,
            null);
    }

    public ObservableCollection<AdditionRowViewModel> Installed { get; } = [];

    public ObservableCollection<AdditionRowViewModel> Available { get; } = [];

    [ObservableProperty]
    private string _status = "尚未读取扩展状态。";

    [ObservableProperty]
    private string _catalogStatus = "官方目录在打开本页时按需读取；普通启动不会访问网络。";

    [ObservableProperty]
    private bool _isBusy;

    public async Task ActivateAsync()
    {
        if (_activated)
        {
            return;
        }

        _activated = true;
        await ReloadAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        _catalog = null;
        await ReloadAsync();
    }

    [RelayCommand]
    private async Task InstallAsync(AdditionRowViewModel? row)
    {
        if (row is null || _catalog is null)
        {
            return;
        }

        var plan = _planner.Plan(
            _catalog.Catalog,
            _state.Load().Document.Additions,
            [row.Id],
            CoreApiVersion,
            OperatingSystem.IsWindows() ? "windows" : "linux");
        if (!plan.IsSuccess)
        {
            Status = plan.Error!.Message;
            return;
        }

        if (plan.Value!.IsEmpty)
        {
            Status = $"{row.Id.Value} 已安装且无需更新。";
            return;
        }

        IsBusy = true;
        try
        {
            foreach (var item in plan.Value.All)
            {
                var installed = await _installer.InstallAsync(item.Entry).ConfigureAwait(true);
                if (!installed.IsSuccess)
                {
                    Status = $"安装 {item.Entry.Id.Value} 失败：{installed.Error!.Message}";
                    return;
                }
            }

            Status = $"已安装/更新 {plan.Value.All.Count} 个扩展。";
            _catalog = null;
            await ReloadAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RemoveAsync(AdditionRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        if (!_dialogs.Confirm("卸载扩展", $"将卸载 {row.Id.Value}。用户数据目录会保留，不会删除实例或存档。", $"additions.remove.{row.Id.Value}"))
        {
            return;
        }

        var result = await _installer.UninstallAsync(row.Id).ConfigureAwait(true);
        Status = result.IsSuccess ? $"已卸载 {row.Id.Value}。" : result.Error!.Message;
        await ReloadAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ToggleEnabledAsync(AdditionRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        var result = await _installer.SetEnabledAsync(row.Id, !row.Enabled).ConfigureAwait(true);
        Status = result.IsSuccess
            ? $"{(row.Enabled ? "已禁用" : "已启用")} {row.Id.Value}。"
            : result.Error!.Message;
        await ReloadAsync().ConfigureAwait(true);
    }

    private async Task ReloadAsync()
    {
        var load = _state.Load();
        Installed.Clear();
        foreach (var item in load.Document.Additions.OrderBy(item => item.Id.Value, StringComparer.Ordinal))
        {
            Installed.Add(AdditionRowViewModel.FromInstalled(item));
        }

        if (load.IsCorrupt)
        {
            Status = load.Error ?? "扩展状态文件损坏；已安装列表可能不完整。";
        }
        else if (Installed.Count == 0)
        {
            Status = "尚未安装任何扩展。";
        }
        else
        {
            Status = $"已安装 {Installed.Count} 个扩展。";
        }

        Available.Clear();
        var catalogUrl = Environment.GetEnvironmentVariable("ECL_ADDITIONS_CATALOG");
        if (string.IsNullOrWhiteSpace(catalogUrl))
        {
            CatalogStatus = "未配置官方目录。设置 ECL_ADDITIONS_CATALOG 为 HTTPS 目录地址后刷新；普通启动不会读取目录。";
            return;
        }

        if (!Uri.TryCreate(catalogUrl, UriKind.Absolute, out var catalogUri) ||
            !string.Equals(catalogUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            CatalogStatus = "ECL_ADDITIONS_CATALOG 必须是 HTTPS 绝对地址。";
            return;
        }

        var signatureValue = Environment.GetEnvironmentVariable("ECL_ADDITIONS_CATALOG_SIGNATURE");
        var signatureUri = Uri.TryCreate(signatureValue, UriKind.Absolute, out var explicitSignature)
            ? explicitSignature
            : new Uri(catalogUri.AbsoluteUri + ".sig");
        var trustPath = Path.Combine(AppContext.BaseDirectory, AdditionCatalogService.TrustFileName);
        var trust = await UpdateTrustStore.FromFileAsync(trustPath).ConfigureAwait(true);
        if (!trust.IsSuccess)
        {
            CatalogStatus = $"缺少可用的扩展目录公钥：{trust.Error!.Message}";
            return;
        }

        if (_catalog is null)
        {
            IsBusy = true;
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                var service = new AdditionCatalogService(
                    new HttpAdditionCatalogSource(http, catalogUri, signatureUri),
                    new AdditionCatalogStore(Path.Combine(_paths.Root, "additions", "catalog")),
                    trust.Value!);
                var snapshot = await service.GetAsync().ConfigureAwait(true);
                if (!snapshot.IsSuccess)
                {
                    CatalogStatus = snapshot.Error!.Message;
                    return;
                }

                _catalog = snapshot.Value!;
            }
            finally
            {
                IsBusy = false;
            }
        }

        CatalogStatus = _catalog.FromCache
            ? "正在使用本地缓存的扩展目录；可能不是最新状态。"
            : "官方扩展目录已通过签名验证。";
        var installedIds = Installed.Select(item => item.Id).ToHashSet();
        foreach (var entry in _catalog.Catalog.Additions.Where(entry => !entry.Withdrawn && !installedIds.Contains(entry.Id)))
        {
            Available.Add(AdditionRowViewModel.FromEntry(entry));
        }

        if (Available.Count == 0)
        {
            CatalogStatus += " 当前没有可安装的新扩展。";
        }
    }
}

public sealed partial class AdditionRowViewModel : ObservableObject
{
    private AdditionRowViewModel(AdditionId id, string name, string version, string description, bool installed, bool enabled, bool canInstall)
    {
        Id = id;
        Name = name;
        Version = version;
        Description = description;
        IsInstalled = installed;
        Enabled = enabled;
        CanInstall = canInstall;
    }

    public AdditionId Id { get; }

    public string Name { get; }

    public string Version { get; }

    public string Description { get; }

    public bool IsInstalled { get; }

    public bool Enabled { get; }

    public bool CanInstall { get; }

    public bool CanRemove => IsInstalled;

    public bool CanToggle => IsInstalled;

    public string StateText => IsInstalled ? (Enabled ? "已安装 · 已启用" : "已安装 · 已禁用") : "可安装";

    public string ToggleText => Enabled ? "禁用" : "启用";

    public static AdditionRowViewModel FromInstalled(InstalledAddition item) =>
        new(item.Id, item.Id.Value, item.Version.ToString(), "已安装扩展", installed: true, item.Enabled, canInstall: false);

    public static AdditionRowViewModel FromEntry(AdditionEntry entry) =>
        new(entry.Id, entry.Name, entry.Version.ToString(), entry.Description, installed: false, enabled: false, canInstall: true);
}

public sealed partial class WardrobeViewModel : ObservableObject
{
    private readonly WardrobeStore _store;
    private readonly IDialogService _dialogs;
    private bool _activated;

    public WardrobeViewModel(IAppPaths paths, IDialogService dialogs)
    {
        _dialogs = dialogs;
        _store = new WardrobeStore(Path.Combine(paths.Root, "wardrobe"));
        TextureTypes = [WardrobeTextureType.Skin, WardrobeTextureType.Cape];
        _selectedType = WardrobeTextureType.Skin;
    }

    public ObservableCollection<WardrobeRowViewModel> Items { get; } = [];

    public IReadOnlyList<WardrobeTextureType> TextureTypes { get; }

    [ObservableProperty]
    private WardrobeTextureType _selectedType;

    [ObservableProperty]
    private WardrobeRowViewModel? _selectedItem;

    [ObservableProperty]
    private string _status = "尚未读取衣柜。";

    [ObservableProperty]
    private bool _isBusy;

    public async Task ActivateAsync()
    {
        if (_activated)
        {
            return;
        }

        _activated = true;
        await RefreshAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        var load = _store.LoadIndex();
        Items.Clear();
        foreach (var item in load.Index.Items
                     .OrderByDescending(item => item.IsFavorite)
                     .ThenByDescending(item => item.ImportedAt))
        {
            Items.Add(new WardrobeRowViewModel(item, _store.ResolveResourcePath(item)));
        }

        SelectedItem = Items.FirstOrDefault();
        Status = load.RecoveredFromCorruption
            ? load.Warning ?? "衣柜索引损坏，已按空索引恢复。"
            : Items.Count == 0
                ? "衣柜为空。导入 PNG 皮肤或披风后在此管理。"
                : $"共 {Items.Count} 项资源。";
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "导入皮肤或披风 PNG",
            Filter = "PNG 图片 (*.png)|*.png",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _store
                .ImportAsync(
                    dialog.FileName,
                    SelectedType,
                    Path.GetFileNameWithoutExtension(dialog.FileName))
                .ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                Status = result.Error!.Message;
                return;
            }

            Status = result.Value!.Created
                ? $"已导入 {result.Value.Item.Name}。"
                : "相同内容已在衣柜中，未重复导入。";
            await RefreshAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ToggleFavoriteAsync(WardrobeRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        var result = await _store.SetFavoriteAsync(row.Item.Id, !row.Item.IsFavorite).ConfigureAwait(true);
        Status = result.IsSuccess
            ? (result.Value!.IsFavorite ? "已收藏。" : "已取消收藏。")
            : result.Error!.Message;
        await RefreshAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task DeleteAsync(WardrobeRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        if (!_dialogs.Confirm("删除衣柜资源", $"将删除衣柜中的“{row.Item.Name}”。此操作不会删除账户或实例配置。", $"wardrobe.delete.{row.Item.Id}"))
        {
            return;
        }

        var result = await _store.DeleteAsync(row.Item.Id).ConfigureAwait(true);
        Status = result.IsSuccess ? "已删除衣柜资源。" : result.Error!.Message;
        await RefreshAsync().ConfigureAwait(true);
    }
}

public sealed partial class WardrobeRowViewModel : ObservableObject
{
    public WardrobeRowViewModel(WardrobeItem item, string path)
    {
        Item = item;
        Preview = LoadPreview(path);
    }

    public WardrobeItem Item { get; }

    public System.Windows.Media.ImageSource? Preview { get; }

    public string Name => Item.Name;

    public string KindText => Item.Type == WardrobeTextureType.Skin ? "皮肤" : "披风";

    public string ModelText => Item.SkinModel switch
    {
        SkinModel.Classic => "Classic",
        SkinModel.Slim => "Slim",
        _ => "模型未知",
    };

    public string SizeText => $"{Item.Width}×{Item.Height}";

    public string HashText => Item.Sha256[..12];

    public string FavoriteText => Item.IsFavorite ? "已收藏" : "收藏";

    private static System.Windows.Media.ImageSource? LoadPreview(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException or UriFormatException)
        {
            return null;
        }
    }
}

public sealed partial class DiagnosticsViewModel : ObservableObject
{
    private readonly IInstanceService _instances;
    private readonly IAppPaths _paths;
    private readonly DiagnosticEngine _engine = new();
    private bool _activated;

    public DiagnosticsViewModel(IInstanceService instances, IAppPaths paths)
    {
        _instances = instances;
        _paths = paths;
    }

    public ObservableCollection<InstanceChoice> Instances { get; } = [];

    [ObservableProperty]
    private InstanceChoice? _selectedInstance;

    [ObservableProperty]
    private string _status = "选择实例后分析本地 latest.log 与崩溃报告。";

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private string _uncertainty = string.Empty;

    [ObservableProperty]
    private string _knowledgeNote = "未安装诊断知识库（Data Addition）：当前只做本地日志/崩溃结构化解析，不进行规则匹配，也不会给出确定根因。";

    [ObservableProperty]
    private bool _isBusy;

    public async Task ActivateAsync()
    {
        if (_activated)
        {
            return;
        }

        _activated = true;
        var instances = await _instances.GetInstancesAsync().ConfigureAwait(true);
        Instances.Clear();
        foreach (var instance in instances)
        {
            Instances.Add(new InstanceChoice(instance.Id, $"{instance.Name} · {instance.GameVersion}"));
        }

        SelectedInstance = Instances.FirstOrDefault();
        Status = Instances.Count == 0 ? "尚未创建实例。" : "选择实例后开始分析。";
    }

    [RelayCommand]
    private async Task AnalyzeAsync()
    {
        if (SelectedInstance is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var instance = await _instances.GetAsync(SelectedInstance.Id).ConfigureAwait(true);
            if (instance is null)
            {
                Status = "实例不存在。";
                return;
            }

            var gameDirectory = InstancePaths.GetGameDirectory(_paths, instance);
            var logFiles = new List<string>();
            var latest = Path.Combine(gameDirectory, "logs", "latest.log");
            if (File.Exists(latest))
            {
                logFiles.Add(latest);
            }

            var crashFiles = new List<string>();
            var crashDirectory = Path.Combine(gameDirectory, "crash-reports");
            if (Directory.Exists(crashDirectory))
            {
                crashFiles.AddRange(Directory.EnumerateFiles(crashDirectory, "*.txt").Take(5));
            }

            var input = new DiagnosticInputLoader().Load(logFiles, crashFiles);
            var report = _engine.Analyze(new DiagnosticAnalysisRequest
            {
                Events = input.Events,
                CrashReports = input.CrashReports,
                InputIssues = input.Issues,
                Environment = new DiagnosticEnvironment
                {
                    MinecraftVersion = instance.GameVersion,
                    Loader = instance.Loader,
                    LoaderVersion = instance.LoaderVersion,
                },
            });

            Summary = $"事件 {input.Events.Count} 条 · 崩溃报告 {input.CrashReports.Count} 份 · 结论 {report.Findings.Count} 条 · 置信度 {report.OverallConfidence}";
            Uncertainty = report.UncertaintyNote ?? string.Empty;
            Status = report.Findings.Count == 0
                ? "已完成本地解析；没有安装知识库，因此不输出猜测性根因。"
                : "分析完成。请结合证据链逐条核对。";
            KnowledgeNote = report.KnowledgeBase is null
                ? "未安装诊断知识库（Data Addition）：当前只做本地日志/崩溃结构化解析，不进行规则匹配。"
                : $"知识库 {report.KnowledgeVersion} 已参与分析。";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
