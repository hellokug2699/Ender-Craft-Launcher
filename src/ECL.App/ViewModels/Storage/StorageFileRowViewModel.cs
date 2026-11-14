// SPDX-License-Identifier: GPL-3.0-or-later

using CommunityToolkit.Mvvm.ComponentModel;
using Ecl.App.Services;
using Ecl.Core.Localization;
using Ecl.Minecraft.Storage;

namespace Ecl.App.ViewModels.Storage;

/// <summary>
/// 存储分类表格中的一行。复选框只对白名单内的项目启用；风险提示统一为“谨慎清理”，
/// 未列入白名单的项目显示保护原因而不是伪造可清理状态。
/// </summary>
public sealed partial class StorageFileRowViewModel : ObservableObject
{
    private readonly ILocalizer _localizer;

    public StorageFileRowViewModel(StorageFileEntry entry, ILocalizer localizer)
    {
        Entry = entry;
        _localizer = localizer;
    }

    public StorageFileEntry Entry { get; }

    public string FileName => Entry.FileName;

    /// <summary>世界存档条目显示“文件夹”，普通日志/截图等条目留空。</summary>
    public string KindTag => Entry.IsDirectory ? _localizer.Get("storage.item.folder") : string.Empty;

    public bool HasKindTag => Entry.IsDirectory;

    public string OwnerText => Entry.Owner ?? string.Empty;

    public string SizeText => Entry.Size is { } size
        ? StorageSizeFormatter.FormatBytes(size)
        : _localizer.Get("storage.sizeUnknown");

    public long? Size => Entry.Size;

    public bool IsSelectable => Entry.CanClean;

    public string SuggestionText => Entry.CanClean
        ? _localizer.Get("storage.suggestion.caution")
        : _localizer.Get("storage.protection." + (Entry.ProtectionReason ?? "userData"));

    public bool HasSuggestion => true;

    public StorageRiskLevel Risk => Entry.Risk;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private string _progressKind = "idle";

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private bool _isProgressVisible;

    [ObservableProperty]
    private string _errorText = string.Empty;

    [ObservableProperty]
    private bool _hasError;

    public void BeginProgress()
    {
        ProgressKind = "running";
        ProgressValue = 0;
        IsProgressVisible = true;
        ErrorText = string.Empty;
        HasError = false;
    }

    public void CompleteProgress(bool success, string? reason)
    {
        ProgressKind = success ? "success" : "failed";
        ProgressValue = 100;
        IsProgressVisible = true;
        ErrorText = success
            ? string.Empty
            : _localizer.Get("storage.delete.reasonPrefix", reason ?? string.Empty);
        HasError = !success;
    }

    public void ResetProgress()
    {
        ProgressKind = "idle";
        ProgressValue = 0;
        IsProgressVisible = false;
        ErrorText = string.Empty;
        HasError = false;
    }
}
