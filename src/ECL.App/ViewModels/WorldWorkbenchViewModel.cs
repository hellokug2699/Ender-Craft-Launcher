// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.App.Services;
using Ecl.App.Views;
using Ecl.Minecraft.World;
using Ecl.World.Diagnostics;
using Ecl.World.Recent;
using Ecl.World.Rendering;
using Ecl.World.Servers;
using Ecl.World.Worlds;

namespace Ecl.App.ViewModels;

public sealed class WorldItemViewModel(WorldSave world)
{
    public WorldSave World { get; } = world;

    public string Name => World.Name;

    public string Folder => World.FolderName;

    public string Version => string.IsNullOrWhiteSpace(World.Level.VersionName)
        ? "版本未知"
        : World.Level.VersionName;

    public string Meta => $"{Version} · {World.Dimensions.Count} 个维度 · {World.Level.LastPlayed?.ToLocalTime():yyyy-MM-dd HH:mm}";

    public string Path => World.DirectoryPath;

    public string? Error => World.Level.Error;
}

public sealed class ServerItemViewModel(ServerEntry entry, ServerPingResult? ping = null)
{
    public ServerEntry Entry { get; } = entry;

    public string Name => Entry.Name;

    public string Address => Entry.DisplayAddress;

    public string Status => Ping?.StatusText ?? "尚未查询";

    public string Players => Ping is { PlayersOnline: { } online, PlayersMax: { } max } ? $"{online}/{max}" : "—";

    public string Motd => Ping?.Motd ?? string.Empty;

    public string Version => Ping?.VersionName ?? string.Empty;

    public string Latency => Ping?.LatencyMilliseconds is { } latency ? $"{latency} ms" : string.Empty;

    public ServerPingResult? Ping { get; } = ping;
}

public sealed class DiagnosticItemViewModel(DiagnosticEvent item)
{
    public DiagnosticEvent Item { get; } = item;

    public string Summary => Item.Summary;

    public string Category => Item.Category switch
    {
        DiagnosticCategory.ChunkLoad => "区块加载",
        DiagnosticCategory.ChunkDecode => "区块解析",
        DiagnosticCategory.NbtDecode => "NBT 解析",
        DiagnosticCategory.EntityTick => "实体 Tick",
        DiagnosticCategory.EntityRender => "实体渲染",
        DiagnosticCategory.RegionAccess => "Region 访问",
        _ => "其它",
    };

    public string Location => Item.Location.HasChunk
        ? $"{Item.Location.DimensionId ?? "维度未知"} · Chunk ({Item.Location.ChunkX}, {Item.Location.ChunkZ}) · {ConfidenceText(Item.Location.Confidence)}"
        : $"{Item.Location.DimensionId ?? "维度未知"} · 未定位";

    public string Risk => Item.Severity switch
    {
        DiagnosticSeverity.Info => "信息",
        DiagnosticSeverity.Low => "低",
        DiagnosticSeverity.Medium => "中",
        DiagnosticSeverity.High => "高",
        _ => "严重",
    };

    public string CountText => Item.OccurrenceCount > 1 ? $"{Item.OccurrenceCount} 次" : string.Empty;

    public string Hypotheses => string.Join("；", Item.Hypotheses.Take(2));

    private static string ConfidenceText(DiagnosticConfidence confidence) => confidence switch
    {
        DiagnosticConfidence.Exact => "明确坐标",
        DiagnosticConfidence.Derived => "坐标推导",
        DiagnosticConfidence.Inferred => "关联推断",
        DiagnosticConfidence.LocationOnly => "位置线索",
        _ => "无法定位",
    };
}

public sealed class RecentItemViewModel(RecentAccessEntry entry)
{
    public RecentAccessEntry Entry { get; } = entry;

    public string Kind => Entry.Kind switch
    {
        RecentAccessKind.World => "存档",
        RecentAccessKind.Server => "服务器",
        _ => "游戏",
    };

    public string Name => Entry.DisplayName;

    public string Detail => string.Join(" · ", new[] { Entry.Detail, Entry.VisitedAtUtc.ToLocalTime().ToString("MM-dd HH:mm") }.Where(value => !string.IsNullOrWhiteSpace(value)));
}

public sealed partial class WorldWorkbenchViewModel : ObservableObject
{
    private readonly WorldWorkbenchService _service;
    private readonly IDialogService _dialogs;
    private readonly string _instanceId;

    public WorldWorkbenchViewModel(
        WorldWorkbenchService service,
        IDialogService dialogs,
        string instanceId,
        string instanceName)
    {
        _service = service;
        _dialogs = dialogs;
        _instanceId = instanceId;
        InstanceName = instanceName;
        Layers =
        [
            new MapLayerChoice(MapLayer.Height, "高度图"),
            new MapLayerChoice(MapLayer.Surface, "表面方块"),
            new MapLayerChoice(MapLayer.Biome, "生物群系"),
            new MapLayerChoice(MapLayer.ChunkStatus, "区块状态"),
        ];
        _selectedLayer = Layers[0];
    }

    public string InstanceName { get; }

    public ObservableCollection<WorldItemViewModel> Worlds { get; } = [];

    public ObservableCollection<ServerItemViewModel> Servers { get; } = [];

    public ObservableCollection<DiagnosticItemViewModel> Diagnostics { get; } = [];

    public ObservableCollection<RecentItemViewModel> Recent { get; } = [];

    public ObservableCollection<DimensionChoice> Dimensions { get; } = [];

    public IReadOnlyList<MapLayerChoice> Layers { get; }

    [ObservableProperty]
    private WorldItemViewModel? _selectedWorld;

    [ObservableProperty]
    private DimensionChoice? _selectedDimension;

    [ObservableProperty]
    private MapLayerChoice _selectedLayer;

    [ObservableProperty]
    private ImageSource? _mapImage;

    [ObservableProperty]
    private int _mapRadius = 4;

    [ObservableProperty]
    private string _mapCenterChunkX = "0";

    [ObservableProperty]
    private string _mapCenterChunkZ = "0";

    [ObservableProperty]
    private string _status = "正在读取实例工作台…";

    [ObservableProperty]
    private string _mapSummary = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    private CancellationTokenSource? _renderCancellation;
    private int _renderGeneration;

    public async Task InitializeAsync()
    {
        await LoadAllAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync() => await LoadAllAsync();

    [RelayCommand]
    private async Task BackupSelectedWorldAsync()
    {
        if (SelectedWorld is null)
        {
            return;
        }

        Status = "正在备份存档…";
        var result = await _service.BackupWorldAsync(_instanceId, SelectedWorld.Folder);
        Status = result is null ? "备份失败：存档不存在。" : result.Message;
    }

    [RelayCommand]
    private async Task DeleteSelectedWorldAsync()
    {
        if (SelectedWorld is null)
        {
            return;
        }

        var accepted = _dialogs.Confirm(
            "删除存档",
            $"将删除存档“{SelectedWorld.Name}”。\n\n路径：{SelectedWorld.Path}\n\n" +
            "这会移除其中的世界数据，且无法通过 ECL 撤销。请确认已不再需要该存档，或先创建备份。",
            $"world.delete.{SelectedWorld.Folder}");
        if (!accepted)
        {
            return;
        }

        var error = await _service.DeleteWorldAsync(_instanceId, SelectedWorld.Folder, confirmed: true);
        Status = error ?? "存档已删除。";
        if (error is null)
        {
            await LoadWorldsAsync();
        }
    }

    [RelayCommand]
    private void OpenLevelDat()
    {
        if (SelectedWorld is null)
        {
            return;
        }

        new NbtExplorerWindow(SelectedWorld.World.LevelDatPath)
        {
            Owner = System.Windows.Application.Current.MainWindow,
        }.ShowDialog();
    }

    [RelayCommand]
    private void OpenWorldFolder()
    {
        if (SelectedWorld is null)
        {
            return;
        }

        Process.Start(new ProcessStartInfo(SelectedWorld.Path) { UseShellExecute = true });
    }

    [RelayCommand]
    private async Task PingServersAsync()
    {
        IsBusy = true;
        Status = "正在查询服务器状态…";
        try
        {
            var results = await _service.PingServersAsync(_instanceId, refresh: true);
            Servers.Clear();
            var list = await _service.ListServersAsync(_instanceId);
            foreach (var entry in list.Servers)
            {
                var ping = results.FirstOrDefault(item =>
                    string.Equals(item.Host, entry.Host, StringComparison.OrdinalIgnoreCase) && item.Port == entry.Port);
                Servers.Add(new ServerItemViewModel(entry, ping));
            }

            await LoadRecentAsync();
            Status = list.IsReadable ? $"已查询 {Servers.Count} 个服务器。" : list.Error ?? "服务器列表不可读。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task AnalyzeDiagnosticsAsync()
    {
        IsBusy = true;
        Status = "正在分析游戏日志与崩溃报告…";
        try
        {
            var result = await _service.AnalyzeDiagnosticsAsync(_instanceId, sessionId: null);
            await LoadDiagnosticsAsync();
            Status = result.Error ?? $"分析完成：{result.Events.Count} 个诊断事件，扫描 {result.LinesScanned} 行。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ClearRecentAsync()
    {
        await _service.ClearRecentAsync();
        await LoadRecentAsync();
        Status = "已清空最近访问记录。";
    }

    [RelayCommand]
    private async Task RenderMapAsync()
    {
        if (SelectedWorld is null || SelectedDimension is null)
        {
            return;
        }

        // ADR 0031 §5：切换维度/世界会取消上一次渲染，并用代号丢弃迟到的旧结果，
        // 避免旧维度的地图或诊断标记覆盖当前视图。
        _renderCancellation?.Cancel();
        _renderCancellation?.Dispose();
        _renderCancellation = new CancellationTokenSource();
        var cancellation = _renderCancellation;
        var generation = ++_renderGeneration;

        IsBusy = true;
        try
        {
            var centerX = int.TryParse(MapCenterChunkX, out var parsedX) ? parsedX : 0;
            var centerZ = int.TryParse(MapCenterChunkZ, out var parsedZ) ? parsedZ : 0;
            var view = await _service.RenderMapAsync(
                _instanceId,
                SelectedWorld.Folder,
                SelectedDimension.Id,
                centerChunkX: centerX,
                centerChunkZ: centerZ,
                MapRadius,
                SelectedLayer.Layer,
                cancellation.Token);
            if (generation != _renderGeneration || cancellation.IsCancellationRequested)
            {
                return;
            }

            if (view is null)
            {
                MapImage = null;
                MapSummary = "找不到存档。";
                return;
            }

            if (view.Failed)
            {
                MapImage = null;
                MapSummary = view.Error ?? "该维度无法读取。";
                Status = view.DimensionStatus;
                return;
            }

            if (view.IsEmpty)
            {
                MapImage = null;
                MapSummary = "没有可渲染的区块数据。";
                return;
            }

            MapImage = ToBitmap(view.Image);
            MapSummary = $"{view.WorldName} · {view.DimensionId} · {view.DimensionStatus} · 缺失 {view.MissingChunks} · 损坏 {view.CorruptChunks} · 未知群系像素 {view.Image.UnknownBiomePixels}";
            Status = "地图只读，不会修改世界数据。";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // 切换维度导致的取消是预期行为。
        }
        finally
        {
            if (generation == _renderGeneration)
            {
                IsBusy = false;
            }
        }
    }

    partial void OnSelectedWorldChanged(WorldItemViewModel? value)
    {
        _renderCancellation?.Cancel();
        _renderGeneration++;
        Dimensions.Clear();
        if (value is null)
        {
            return;
        }

        foreach (var dimension in value.World.Dimensions)
        {
            Dimensions.Add(new DimensionChoice(dimension.Id, dimension.DisplayName, dimension.StorageStatus, dimension.CanRead));
        }

        // 优先选中可读维度；仍然保留全部维度（含缺失/未确认/冲突），由状态文字明确表达。
        SelectedDimension = Dimensions.FirstOrDefault(dimension => dimension.CanRead) ?? Dimensions.FirstOrDefault();
        _ = _service.RecordWorldVisitAsync(_instanceId, value.World);
        _ = RenderMapAsync();
    }

    partial void OnSelectedDimensionChanged(DimensionChoice? value)
    {
        if (value is not null)
        {
            _ = RenderMapAsync();
        }
    }

    partial void OnSelectedLayerChanged(MapLayerChoice value)
    {
        _ = RenderMapAsync();
    }

    private async Task LoadAllAsync()
    {
        IsBusy = true;
        Status = "正在读取存档、服务器与诊断记录…";
        try
        {
            await LoadWorldsAsync();
            await LoadServersAsync();
            await LoadDiagnosticsAsync();
            await LoadRecentAsync();
            Status = Worlds.Count == 0
                ? "该实例还没有本地存档。启动游戏创建世界后会在此显示。"
                : $"已读取 {Worlds.Count} 个存档、{Servers.Count} 个服务器、{Diagnostics.Count} 个诊断事件。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadWorldsAsync()
    {
        var worlds = await _service.ListWorldsAsync(_instanceId);
        Worlds.Clear();
        foreach (var world in worlds)
        {
            Worlds.Add(new WorldItemViewModel(world));
        }

        SelectedWorld = Worlds.FirstOrDefault();
    }

    private async Task LoadServersAsync()
    {
        var list = await _service.ListServersAsync(_instanceId);
        Servers.Clear();
        foreach (var entry in list.Servers)
        {
            Servers.Add(new ServerItemViewModel(entry));
        }
    }

    private async Task LoadDiagnosticsAsync()
    {
        var events = await _service.LoadDiagnosticsAsync(_instanceId);
        Diagnostics.Clear();
        foreach (var item in events)
        {
            Diagnostics.Add(new DiagnosticItemViewModel(item));
        }
    }

    private async Task LoadRecentAsync()
    {
        var entries = await _service.RecentAsync(_instanceId);
        Recent.Clear();
        foreach (var entry in entries)
        {
            Recent.Add(new RecentItemViewModel(entry));
        }
    }

    private static ImageSource ToBitmap(MapImage image)
    {
        var pixels = new byte[image.Pixels.Length * 4];
        for (var index = 0; index < image.Pixels.Length; index++)
        {
            var pixel = image.Pixels[index];
            pixels[index * 4] = (byte)(pixel & 0xFF);
            pixels[index * 4 + 1] = (byte)((pixel >> 8) & 0xFF);
            pixels[index * 4 + 2] = (byte)((pixel >> 16) & 0xFF);
            pixels[index * 4 + 3] = (byte)((pixel >> 24) & 0xFF);
        }

        var bitmap = BitmapSource.Create(
            image.Width,
            image.Height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            image.Width * 4);
        bitmap.Freeze();
        return bitmap;
    }
}

public sealed record DimensionChoice(string Id, string Name, string Status, bool CanRead)
{
    /// <summary>下拉框显示：可读性用状态文字表达，不隐藏未确认或缺失维度。</summary>
    public string DisplayName => $"{Name} · {Status}";
}

public sealed record MapLayerChoice(MapLayer Layer, string Name);
