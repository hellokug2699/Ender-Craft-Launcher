// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.App.Services;
using Ecl.Core;
using Ecl.Core.Localization;
using Ecl.Minecraft.Schematics;

namespace Ecl.App.Workspace.ViewModels;

/// <summary>投影文件的格式识别呈现等级；只影响图标与提示，不参与支持检测。</summary>
public enum SchematicRecognitionKind
{
    Recognized,
    Unverified,
    Unreadable,
}

/// <summary>
/// 一个投影文件的只读行状态。格式、识别状态与大小都来自领域服务；
/// 页面不自行按扩展名猜测格式，也不生成三维缩略图。
/// </summary>
public sealed partial class SchematicItemViewModel : ObservableObject
{
    public SchematicItemViewModel(SchematicEntry entry, ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(localizer);

        Entry = entry;
        FileName = entry.FileName;
        RelativePath = entry.RelativePath;
        SizeText = StorageSizeFormatter.FormatBytes(entry.Size);
        TimeText = entry.ModifiedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        FormatText = localizer.Get(FormatKey(entry.Format));
        FormatBadge = FormatBadgeText(entry.Format);
        RecognitionKind = entry.Recognition switch
        {
            SchematicRecognition.Recognized => SchematicRecognitionKind.Recognized,
            SchematicRecognition.Unverified => SchematicRecognitionKind.Unverified,
            _ => SchematicRecognitionKind.Unreadable,
        };
        RecognitionText = localizer.Get(RecognitionKind switch
        {
            SchematicRecognitionKind.Unverified => "schematics.recognition.unverified",
            SchematicRecognitionKind.Unreadable => "schematics.recognition.unreadable",
            _ => "schematics.recognition.recognized",
        });
        SourceText = DescribeSource(entry.RelativePath);
    }

    public SchematicEntry Entry { get; }

    public string FileName { get; }

    public string RelativePath { get; }

    /// <summary>列表主行不展开完整相对路径；悬停可看到相对路径与来源目录。</summary>
    public string FullPathHint => RelativePath;

    public string SourceText { get; }

    public string FormatText { get; }

    /// <summary>文件类型徽标文字（LIT/SCHEM/MCE/NBT/BP），不使用 emoji。</summary>
    public string FormatBadge { get; }

    public string SizeText { get; }

    public string TimeText { get; }

    public SchematicRecognitionKind RecognitionKind { get; }

    public string RecognitionText { get; }

    [ObservableProperty]
    private bool _isSelected;

    internal static string FormatKey(SchematicFormat format) => format switch
    {
        SchematicFormat.Litematica => "schematics.format.litematic",
        SchematicFormat.SpongeSchematic => "schematics.format.schem",
        SchematicFormat.MceSchematic => "schematics.format.schematic",
        SchematicFormat.NbtStructure => "schematics.format.nbt",
        _ => "schematics.format.bp",
    };

    private static string FormatBadgeText(SchematicFormat format) => format switch
    {
        SchematicFormat.Litematica => "LIT",
        SchematicFormat.SpongeSchematic => "SCHEM",
        SchematicFormat.MceSchematic => "MCE",
        SchematicFormat.NbtStructure => "NBT",
        _ => "BP",
    };

    private static string DescribeSource(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        if (normalized.StartsWith("config/worldedit/", StringComparison.OrdinalIgnoreCase))
        {
            return "WorldEdit";
        }

        if (normalized.StartsWith("config/axiom/", StringComparison.OrdinalIgnoreCase))
        {
            return "Axiom";
        }

        return "schematics";
    }
}

/// <summary>一个投影格式的支持行；状态与原因全部来自检测结果。</summary>
public sealed class SchematicFormatSupportViewModel
{
    public SchematicFormatSupportViewModel(SchematicFormatSupport support, ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(support);
        ArgumentNullException.ThrowIfNull(localizer);

        Format = SchematicItemViewModel.FormatKey(support.Format);
        FormatText = localizer.Get(Format);
        StateText = localizer.Get(StateKey(support.State));
        ProviderText = support.Providers.Count == 0
            ? localizer.Get("schematics.support.noProvider")
            : string.Join(" / ", support.Providers);
        DetailText = DescribeDetail(support, localizer);
        StatusKind = ToStatusKind(support.State);
    }

    public string Format { get; }

    public string FormatText { get; }

    public string StateText { get; }

    public string ProviderText { get; }

    public string DetailText { get; }

    public bool HasDetail => DetailText.Length > 0;

    public ContentPackStatusKind StatusKind { get; }

    public static string StateKey(SchematicSupportState state) => state switch
    {
        SchematicSupportState.Supported => "schematics.support.state.supported",
        SchematicSupportState.Incomplete => "schematics.support.state.incomplete",
        SchematicSupportState.Incompatible => "schematics.support.state.incompatible",
        SchematicSupportState.Conflict => "schematics.support.state.conflict",
        SchematicSupportState.Unknown => "schematics.support.state.unknown",
        _ => "schematics.support.state.missing",
    };

    public static ContentPackStatusKind ToStatusKind(SchematicSupportState state) => state switch
    {
        SchematicSupportState.Supported => ContentPackStatusKind.Active,
        SchematicSupportState.Incomplete => ContentPackStatusKind.Warning,
        SchematicSupportState.Incompatible => ContentPackStatusKind.Corrupt,
        SchematicSupportState.Conflict => ContentPackStatusKind.Corrupt,
        _ => ContentPackStatusKind.Unrecognized,
    };

    private static string DescribeDetail(SchematicFormatSupport support, ILocalizer localizer) =>
        support.State switch
        {
            SchematicSupportState.Incomplete => localizer.Get(
                "schematics.support.detail.missingDependency",
                support.Detail ?? localizer.Get("schematics.support.detail.unknownDependency")),
            SchematicSupportState.Incompatible => localizer.Get(
                "schematics.support.detail.artifactMismatch"),
            SchematicSupportState.Unknown => localizer.Get(
                "schematics.support.detail.artifactUnverified"),
            SchematicSupportState.Conflict => localizer.Get(
                "schematics.support.detail.conflict",
                support.Detail ?? string.Empty),
            _ => string.Empty,
        };
}

/// <summary>
/// 实例工作区「世界与文件 · 投影」页面（B2c，schematics.md）。
/// <para>
/// 列表式文件管理 + 按格式的真实支持检测；没有渲染 Addition 时不显示三维预览，
/// NBT Explorer 未安装/未提供时明确显示不可用。删除由领域服务在实例事务中执行。
/// </para>
/// </summary>
public sealed partial class SchematicPageViewModel : ObservableObject, IDisposable
{
    private readonly string _instanceId;
    private readonly ISchematicService _schematics;
    private readonly ISchematicSupportService _support;
    private readonly IDialogService _dialogs;
    private readonly ILocalizer _localizer;

    public SchematicPageViewModel(
        string instanceId,
        ISchematicService schematics,
        ISchematicSupportService support,
        IDialogService dialogs,
        ILocalizer localizer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentNullException.ThrowIfNull(schematics);
        ArgumentNullException.ThrowIfNull(support);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(localizer);

        _instanceId = instanceId;
        _schematics = schematics;
        _support = support;
        _dialogs = dialogs;
        _localizer = localizer;
    }

    public string InstanceId => _instanceId;

    public ObservableCollection<SchematicItemViewModel> Items { get; } = [];

    public ObservableCollection<SchematicFormatSupportViewModel> FormatSupport { get; } = [];

    public bool IsDisposed { get; private set; }

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private string _warningMessage = string.Empty;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private SchematicSupportState? _supportState;

    [ObservableProperty]
    private string _supportReason = string.Empty;

    [ObservableProperty]
    private string _supportProviders = string.Empty;

    [ObservableProperty]
    private string _supportError = string.Empty;

    public string Title => _localizer.Get("instance.nav.schematics");

    public string Subtitle => _localizer.Get("schematics.subtitle");

    public string CountText => _localizer.Get("schematics.count", Items.Count);

    public string EmptyTitle => _localizer.Get("schematics.empty.title");

    public string EmptyDescription => _localizer.Get("schematics.empty.description");

    public string LoadFailedText => _localizer.Get("schematics.loadFailed");

    public string DeleteButtonText => SelectedCount > 0
        ? _localizer.Get("schematics.deleteCount", SelectedCount)
        : _localizer.Get("common.delete");

    public string SelectionSummary => _localizer.Get("schematics.selected", SelectedCount);

    public string SelectedSizeText => StorageSizeFormatter.FormatBytes(
        Items.Where(item => item.IsSelected).Sum(item => item.Entry.Size));

    public string SupportTitle => _localizer.Get("schematics.support.title");

    public string SupportNote => _localizer.Get("schematics.support.note");

    public string SupportStateText => SupportState is { } state
        ? _localizer.Get(FormatSupportViewModelStateKey(state))
        : _localizer.Get("schematics.support.state.unknown");

    public string SupportProvidersText => string.IsNullOrEmpty(SupportProviders)
        ? _localizer.Get("schematics.support.noProvider")
        : _localizer.Get("schematics.support.providers", SupportProviders);

    public ContentPackStatusKind SupportGlyphKind => SupportState is { } state
        ? SchematicFormatSupportViewModel.ToStatusKind(state)
        : ContentPackStatusKind.Unrecognized;

    public string NbtExplorerTitle => _localizer.Get("schematics.tools.nbt");

    public string NbtExplorerHint => _localizer.Get("schematics.tools.nbtHint");

    public string RendererTitle => _localizer.Get("schematics.tools.renderer");

    public string RendererHint => _localizer.Get("schematics.tools.rendererHint");

    public string ToolsTitle => _localizer.Get("schematics.tools.title");

    public bool HasItems => Items.Count > 0;

    public bool IsEmpty => !IsLoading && Items.Count == 0 && string.IsNullOrEmpty(ErrorMessage);

    public bool HasSelection => SelectedCount > 0;

    public bool HasSupportDetail => FormatSupport.Count > 0;

    public int SelectedCount => Items.Count(item => item.IsSelected);

    public async Task InitializeAsync()
    {
        if (IsDisposed)
        {
            return;
        }

        await ReloadAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsDisposed || IsBusy || IsLoading)
        {
            return;
        }

        StatusMessage = string.Empty;
        await ReloadAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void ToggleSelectAll()
    {
        if (IsDisposed || IsBusy || Items.Count == 0)
        {
            return;
        }

        var select = SelectedCount < Items.Count;
        foreach (var item in Items)
        {
            item.IsSelected = select;
        }
    }

    [RelayCommand(CanExecute = nameof(CanDeleteSelection))]
    private async Task DeleteSelectedAsync()
    {
        if (IsDisposed || IsBusy)
        {
            return;
        }

        var selected = Items.Where(item => item.IsSelected).ToArray();
        if (selected.Length == 0)
        {
            return;
        }

        var total = selected.Sum(item => item.Entry.Size);
        if (!_dialogs.Confirm(
                _localizer.Get("schematics.deleteTitle"),
                _localizer.Get("schematics.deleteConfirm", selected.Length, StorageSizeFormatter.FormatBytes(total))))
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _schematics
                .DeleteAsync(_instanceId, selected.Select(item => item.RelativePath).ToArray())
                .ConfigureAwait(true);
            if (IsDisposed)
            {
                return;
            }

            if (!result.IsSuccess)
            {
                StatusMessage = result.Error?.ToString() ?? _localizer.Get("schematics.deleteFailed");
                return;
            }

            await ReloadAsync().ConfigureAwait(true);
            if (!IsDisposed)
            {
                StatusMessage = _localizer.Get("schematics.deleted", selected.Length);
            }
        }
        finally
        {
            if (!IsDisposed)
            {
                IsBusy = false;
                DeleteSelectedCommand.NotifyCanExecuteChanged();
            }
        }
    }

    private bool CanDeleteSelection() => !IsDisposed && !IsBusy && HasSelection;

    private async Task ReloadAsync()
    {
        if (IsDisposed)
        {
            return;
        }

        IsLoading = true;
        ErrorMessage = string.Empty;
        WarningMessage = string.Empty;
        try
        {
            var listTask = _schematics.ListAsync(_instanceId);
            var supportTask = _support.DetectAsync(_instanceId);
            await Task.WhenAll(listTask, supportTask).ConfigureAwait(true);
            if (IsDisposed)
            {
                return;
            }

            ClearItems();
            var list = listTask.Result;
            if (!list.IsSuccess)
            {
                ErrorMessage = list.Error?.ToString() ?? LoadFailedText;
            }
            else
            {
                foreach (var entry in list.Value!.Items.OrderBy(entry => entry.FileName, StringComparer.OrdinalIgnoreCase))
                {
                    AddItem(new SchematicItemViewModel(entry, _localizer));
                }

                WarningMessage = list.Value.Warning ?? string.Empty;
            }

            ApplySupport(supportTask.Result);
        }
        finally
        {
            if (!IsDisposed)
            {
                IsLoading = false;
                NotifyCollectionState();
            }
        }
    }

    private void ApplySupport(Result<SchematicSupportResult> result)
    {
        if (!result.IsSuccess)
        {
            SupportState = null;
            SupportError = result.Error?.ToString() ?? _localizer.Get("schematics.support.detectFailed");
            SupportReason = string.Empty;
            SupportProviders = string.Empty;
            FormatSupport.Clear();
            NotifySupportPresentation();
            return;
        }

        var support = result.Value!;
        SupportState = support.State;
        SupportError = string.Empty;
        SupportReason = _localizer.Get(StateReasonKey(support.State));
        SupportProviders = string.Join(" / ", support.Providers);
        FormatSupport.Clear();
        foreach (var format in support.Formats)
        {
            FormatSupport.Add(new SchematicFormatSupportViewModel(format, _localizer));
        }

        NotifySupportPresentation();
    }

    private void NotifySupportPresentation()
    {
        OnPropertyChanged(nameof(SupportState));
        OnPropertyChanged(nameof(SupportStateText));
        OnPropertyChanged(nameof(SupportReason));
        OnPropertyChanged(nameof(SupportProvidersText));
        OnPropertyChanged(nameof(SupportError));
        OnPropertyChanged(nameof(SupportGlyphKind));
        OnPropertyChanged(nameof(HasSupportDetail));
    }

    private void AddItem(SchematicItemViewModel item)
    {
        item.PropertyChanged += OnItemPropertyChanged;
        Items.Add(item);
    }

    private void ClearItems()
    {
        foreach (var item in Items)
        {
            item.PropertyChanged -= OnItemPropertyChanged;
        }

        Items.Clear();
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(SchematicItemViewModel.IsSelected))
        {
            return;
        }

        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedSizeText));
        OnPropertyChanged(nameof(DeleteButtonText));
        OnPropertyChanged(nameof(SelectionSummary));
        DeleteSelectedCommand.NotifyCanExecuteChanged();
    }

    private void NotifyCollectionState()
    {
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedSizeText));
        OnPropertyChanged(nameof(DeleteButtonText));
        OnPropertyChanged(nameof(SelectionSummary));
        DeleteSelectedCommand.NotifyCanExecuteChanged();
    }

    private static string FormatSupportViewModelStateKey(SchematicSupportState state) =>
        SchematicFormatSupportViewModel.StateKey(state);

    private static string StateReasonKey(SchematicSupportState state) => state switch
    {
        SchematicSupportState.Supported => "schematics.support.reason.supported",
        SchematicSupportState.Incomplete => "schematics.support.reason.incomplete",
        SchematicSupportState.Incompatible => "schematics.support.reason.incompatible",
        SchematicSupportState.Conflict => "schematics.support.reason.conflict",
        SchematicSupportState.Unknown => "schematics.support.reason.unknown",
        _ => "schematics.support.reason.missing",
    };

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(IsEmpty));

    partial void OnErrorMessageChanged(string value) => OnPropertyChanged(nameof(IsEmpty));

    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        IsDisposed = true;
        ClearItems();
        FormatSupport.Clear();
    }
}
