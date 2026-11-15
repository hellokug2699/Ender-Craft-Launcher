// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.App.Services;
using Ecl.Core.Localization;

namespace Ecl.App.Dialogs.ViewModels;

/// <summary>
/// 「安装到其他位置」的目标选择（`download-mods.md` §7.1 / §7.2、
/// `download-resource-template.md` §8）：已登记实例始终可选；只有模组允许再选择目录，
/// 因为资源包/光影包/数据包必须安装到已登记实例（模板 §5）。
/// </summary>
public sealed partial class ContentTargetDialogViewModel : ObservableObject, IDialogSessionAware
{
    private readonly IDialogService _dialogs;
    private readonly ILocalizer _localizer;
    private readonly string _pickFolderTitle;
    private IDialogSession? _session;

    public ContentTargetDialogViewModel(
        IReadOnlyList<ContentTargetInstanceRow> instances,
        bool allowDirectory,
        IDialogService dialogs,
        ILocalizer localizer,
        string pickFolderTitle)
    {
        Instances = new ObservableCollection<ContentTargetInstanceRow>(instances);
        AllowDirectory = allowDirectory;
        _dialogs = dialogs;
        _localizer = localizer;
        _pickFolderTitle = pickFolderTitle;
        _selectedInstance = Instances.FirstOrDefault();
    }

    public ObservableCollection<ContentTargetInstanceRow> Instances { get; }

    public bool AllowDirectory { get; }

    public bool HasInstances => Instances.Count > 0;

    public bool IsEmpty => Instances.Count == 0 && PickedDirectory is null;

    public string InstanceSectionText => _localizer.Get("downloads.discovery.other.instances");

    public string EmptyText => _localizer.Get("downloads.discovery.other.none");

    public string DirectoryButtonText => _localizer.Get("downloads.discovery.other.directory");

    public string DirectoryHintText => _localizer.Get("downloads.discovery.other.directoryHint");

    public string ConfirmText => _localizer.Get("downloads.discovery.other.confirm");

    public string CancelText => _localizer.Get("dialog.cancel");

    public string PickedDirectoryText => string.IsNullOrWhiteSpace(PickedDirectory)
        ? _localizer.Get("downloads.discovery.other.notPicked")
        : PickedDirectory;

    public bool HasPickedDirectory => !string.IsNullOrWhiteSpace(PickedDirectory);

    [ObservableProperty]
    private ContentTargetInstanceRow? _selectedInstance;

    [ObservableProperty]
    private string? _pickedDirectory;

    public bool CanConfirm => SelectedInstance is not null || HasPickedDirectory;

    void IDialogSessionAware.Attach(IDialogSession session) => _session = session;

    [RelayCommand]
    private void PickDirectory()
    {
        var folder = _dialogs.PickFolder(_pickFolderTitle);
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        PickedDirectory = folder;
        SelectedInstance = null;
    }

    [RelayCommand]
    private void Confirm()
    {
        if (SelectedInstance is { } instance)
        {
            _session?.TryClose(
                DialogOutcome.Primary,
                new ContentTargetChoice(instance.Id, instance.Name, null));
            return;
        }

        if (PickedDirectory is { Length: > 0 } directory)
        {
            _session?.TryClose(
                DialogOutcome.Primary,
                new ContentTargetChoice(null, null, directory));
        }
    }

    [RelayCommand]
    private void Cancel() => _session?.TryClose(DialogOutcome.Cancel);

    partial void OnSelectedInstanceChanged(ContentTargetInstanceRow? value)
    {
        if (value is not null && PickedDirectory is not null)
        {
            // 实例与目录二选一：选回实例时清掉之前的目录，避免确认时目标歧义。
            PickedDirectory = null;
        }

        OnPropertyChanged(nameof(CanConfirm));
        OnPropertyChanged(nameof(IsEmpty));
    }

    partial void OnPickedDirectoryChanged(string? value)
    {
        OnPropertyChanged(nameof(CanConfirm));
        OnPropertyChanged(nameof(PickedDirectoryText));
        OnPropertyChanged(nameof(HasPickedDirectory));
        OnPropertyChanged(nameof(IsEmpty));
    }
}

/// <summary>一个可选的已登记实例；<see cref="Meta"/> 只写真实版本与加载器。</summary>
public sealed record ContentTargetInstanceRow(string Id, string Name, string Meta)
{
    /// <summary>UIA/读屏只看到实例名称，不暴露记录类型的默认 ToString()。</summary>
    public override string ToString() => Name;
}

/// <summary>目标选择结果：实例或目录二选一；两者都为 null 不属于成功结果。</summary>
public sealed record ContentTargetChoice(string? InstanceId, string? InstanceName, string? Directory);
