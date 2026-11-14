// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.App.Services;
using Ecl.Core;
using Ecl.Diagnostics;
using Ecl.Minecraft.Instances;
using Ecl.Wardrobe;

namespace Ecl.App.ViewModels;

/// <summary>工具箱页：原生承载衣柜与诊断入口；知识和原版工具展示真实可用状态。</summary>
public sealed partial class ToolsViewModel : ObservableObject
{
    public ToolsViewModel(
        WardrobeViewModel wardrobe,
        DiagnosticsViewModel diagnostics,
        EngineToolsViewModel engine)
    {
        Wardrobe = wardrobe;
        Diagnostics = diagnostics;
        Engine = engine;
    }

    public WardrobeViewModel Wardrobe { get; }

    public DiagnosticsViewModel Diagnostics { get; }

    public EngineToolsViewModel Engine { get; }

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
        await Wardrobe.ActivateAsync().ConfigureAwait(true);
        await Diagnostics.ActivateAsync().ConfigureAwait(true);
        await Engine.ActivateAsync().ConfigureAwait(true);
    }
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
