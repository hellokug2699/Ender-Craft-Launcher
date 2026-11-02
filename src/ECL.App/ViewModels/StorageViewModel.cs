// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.App.Services;
using Ecl.Core.Storage;
using Ecl.Minecraft.Storage;
using Microsoft.Win32;

namespace Ecl.App.ViewModels;

public sealed record StorageUsageItemViewModel(StorageUsage Usage)
{
    public string Title => Usage.Label;

    public string SizeText => Usage.Bytes is { } bytes ? FormatBytes(bytes) : Usage.Status switch
    {
        StorageUsageStatus.Unavailable => "不可访问",
        StorageUsageStatus.NotScanned => "尚未扫描",
        StorageUsageStatus.Overlapping => "已排除重复",
        _ => "未知",
    };

    public string StatusText => Usage.Status switch
    {
        StorageUsageStatus.Scanned => "已扫描",
        StorageUsageStatus.Estimated => "估算",
        StorageUsageStatus.Unavailable => "不可访问",
        StorageUsageStatus.Overlapping => "重复排除",
        _ => "未扫描",
    };

    public string Detail => string.IsNullOrWhiteSpace(Usage.Detail)
        ? Usage.Path ?? string.Empty
        : $"{Usage.Detail}  ·  {Usage.Path}";

    public bool IsCleanable => Usage.IsCleanable;

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024 * 1024):0.##} GB",
        >= 1024L * 1024 => $"{bytes / (1024d * 1024):0.##} MB",
        >= 1024 => $"{bytes / 1024d:0.##} KB",
        _ => $"{bytes} B",
    };
}

public sealed record SourceGroupViewModel(
    string Name,
    string Path,
    string Status,
    int InstanceCount,
    IReadOnlyList<string> InstanceNames)
{
    public string InstancesText => InstanceCount == 0
        ? "未发现已识别实例"
        : $"{InstanceCount} 个实例：{string.Join("、", InstanceNames.Take(4))}{(InstanceCount > 4 ? "…" : string.Empty)}";
}

public sealed record CleanupItemViewModel(CleanupPreviewItem Item)
{
    public string Title => Item.Kind switch
    {
        CleanupItemKind.DownloadPart => "下载临时产物",
        CleanupItemKind.RebuildableCache => "可重建缓存",
        CleanupItemKind.InstanceLog => "实例历史日志",
        CleanupItemKind.DiagnosticTemp => "诊断临时文件",
        _ => Item.Kind.ToString(),
    };

    public string SizeText => Item.Bytes >= 1024 * 1024
        ? $"{Item.Bytes / (1024d * 1024):0.##} MB"
        : $"{Math.Max(1, Item.Bytes / 1024d):0.##} KB";

    public string PathText => Item.Path;

    public string Reason => Item.Reason;
}

public sealed partial class StorageViewModel : ObservableObject
{
    private readonly StorageAnalysisService _analysis;
    private readonly StorageCleanupService _cleanup;
    private readonly InstanceSourceService _sources;
    private readonly IWorkspaceService _workspace;
    private readonly IDialogService _dialogs;
    private CleanupPreview? _preview;
    private bool _activated;

    public StorageViewModel(
        StorageAnalysisService analysis,
        StorageCleanupService cleanup,
        InstanceSourceService sources,
        IWorkspaceService workspace,
        IDialogService dialogs)
    {
        _analysis = analysis;
        _cleanup = cleanup;
        _sources = sources;
        _workspace = workspace;
        _dialogs = dialogs;
    }

    public ObservableCollection<StorageUsageItemViewModel> Usages { get; } = [];

    public ObservableCollection<SourceGroupViewModel> SourceGroups { get; } = [];

    public ObservableCollection<CleanupItemViewModel> PreviewItems { get; } = [];

    [ObservableProperty]
    private string _status = "点击“扫描”统计 ECL、下载缓存、Java Runtime、工作区与已登记实例来源。";

    [ObservableProperty]
    private string _summary = "尚未扫描";

    [ObservableProperty]
    private string _previewSummary = "尚未生成清理预览";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _hasPreview;

    public async Task ActivateAsync()
    {
        if (_activated)
        {
            return;
        }

        _activated = true;
        await ScanAsync();
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        Status = "正在扫描存储占用…";
        try
        {
            var result = await _analysis.AnalyzeAsync();
            Usages.Clear();
            foreach (var usage in result.Usages.OrderByDescending(usage => usage.Bytes ?? -1))
            {
                Usages.Add(new StorageUsageItemViewModel(usage));
            }

            Summary = $"已知占用 {FormatBytes(result.KnownBytes)} · {result.InstanceCount} 个已识别实例 · {result.UnavailableCount} 项不可访问";
            Status = result.Warnings.Count == 0
                ? "扫描完成。未知/不可访问项不会被当作 0 字节。"
                : $"扫描完成，{result.Warnings.Count} 条提示：{string.Join("；", result.Warnings.Take(3))}";

            var catalog = await _sources.ScanAllAsync();
            SourceGroups.Clear();
            foreach (var group in catalog.Groups)
            {
                SourceGroups.Add(new SourceGroupViewModel(
                    group.Source.Name,
                    group.Source.Path,
                    group.IsAccessible ? group.Structure.ToString() : "不可访问",
                    group.Instances.Count,
                    group.Instances.Select(instance => instance.Name).ToArray()));
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task AddSourceAsync()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "登记包含 Minecraft 实例的来源目录",
            Multiselect = false,
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var result = await _workspace.RegisterExternalAsync(dialog.FolderName);
        if (!result.IsSuccess)
        {
            _dialogs.ShowError("无法登记来源", result.Error!.Message);
            return;
        }

        Status = "已登记来源。ECL 只读取该目录，不复制、移动或清理其中的实例。";
        await ScanAsync();
    }

    [RelayCommand]
    private async Task PreviewCleanupAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            _preview = await _cleanup.PreviewAsync(new StorageCleanupRequest());
            PreviewItems.Clear();
            foreach (var item in _preview.Items)
            {
                PreviewItems.Add(new CleanupItemViewModel(item));
            }

            HasPreview = _preview.HasItems;
            PreviewSummary = _preview.HasItems
                ? $"可释放约 {FormatBytes(_preview.TotalBytes)}，共 {_preview.Items.Count} 项；跳过 {_preview.SkippedCount} 项（活动下载或未到保留期）。"
                : "没有符合保留期与白名单的清理项。";
            Status = "预览不会删除任何文件。世界存档、模组、配置、资源包与未知文件不在清理范围。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ExecuteCleanupAsync()
    {
        if (_preview is null || !_preview.HasItems || IsBusy)
        {
            return;
        }

        var accepted = _dialogs.Confirm(
            "执行缓存清理",
            $"将删除 {_preview.Items.Count} 项、约 {FormatBytes(_preview.TotalBytes)} 的可重建缓存/临时产物。\n\n" +
            "不会删除世界存档、模组、配置、资源包、截图或未知文件。实例历史日志会被删除。\n\n是否继续？",
            "storage.cleanup.v1");
        if (!accepted)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _cleanup.ExecuteAsync(_preview);
            Status = result.Failed == 0
                ? $"清理完成：删除 {result.Deleted} 项，释放 {FormatBytes(result.BytesFreed)}，跳过 {result.Skipped} 项。"
                : $"清理部分完成：删除 {result.Deleted} 项，失败 {result.Failed} 项。{string.Join("；", result.Errors.Take(2))}";
            _preview = null;
            HasPreview = false;
            PreviewItems.Clear();
            PreviewSummary = "清理已执行。重新扫描可查看最新占用。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024 * 1024):0.##} GB",
        >= 1024L * 1024 => $"{bytes / (1024d * 1024):0.##} MB",
        >= 1024 => $"{bytes / 1024d:0.##} KB",
        _ => $"{bytes} B",
    };
}
