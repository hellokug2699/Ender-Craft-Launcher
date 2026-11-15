// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.App.Dialogs;
using Ecl.App.Services;
using Ecl.Core.Localization;
using Ecl.Core.Storage;

namespace Ecl.App.Dialogs.ViewModels;

/// <summary>
/// “添加 MC 目录”弹窗内容（instances.md §4）。
///
/// 只做目录登记：校验目录名是否为 <c>.minecraft</c>、禁止重复添加、允许解除登记；
/// 不扫描目录内部是否存在可识别实例（§4.3 明确不校验）。
/// 破坏性分支（删除实例）不在此直接执行，而是把目录路径交给调用方，
/// 由调用方沿用实例卡片“删除实例”的同一警告与确认流程。
/// </summary>
public sealed partial class InstanceDirectoryDialogViewModel : ObservableObject, IDialogSessionAware
{
    private readonly IWorkspaceService _workspace;
    private readonly InstanceSourceService _sources;
    private readonly IDialogEngine _dialogs;
    private readonly IDialogService _fileDialogs;
    private readonly ILocalizer _localizer;
    private readonly Func<string, Task>? _deleteInstancesInDirectory;
    private IDialogSession? _session;

    public InstanceDirectoryDialogViewModel(
        IWorkspaceService workspace,
        InstanceSourceService sources,
        IDialogEngine dialogs,
        IDialogService fileDialogs,
        ILocalizer localizer,
        Func<string, Task>? deleteInstancesInDirectory = null)
    {
        _workspace = workspace;
        _sources = sources;
        _dialogs = dialogs;
        _fileDialogs = fileDialogs;
        _localizer = localizer;
        _deleteInstancesInDirectory = deleteInstancesInDirectory;
    }

    public string Title => _localizer.Get("instanceDirectories.title");

    public string EmptyText => _localizer.Get("instanceDirectories.empty");

    public string PickHint => _localizer.Get("instanceDirectories.pick");

    public string DoneText => _localizer.Get("dialog.ok");

    public string RemoveText => _localizer.Get("instanceDirectories.remove");

    public ObservableCollection<InstanceDirectoryRowViewModel> Directories { get; } = [];

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>最近一次目录选择无效；虚线入口随之变红（§4.3）。</summary>
    [ObservableProperty]
    private bool _hasSelectionError;

    [ObservableProperty]
    private string _errorText = string.Empty;

    public bool IsEmpty => Directories.Count == 0;

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);

    void IDialogSessionAware.Attach(IDialogSession session) => _session = session;

    /// <summary>读取当前登记目录及其识别到的实例数量。</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        _session?.SetBusy(true);
        try
        {
            Directories.Clear();
            foreach (var root in _workspace.Roots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = 0;
                try
                {
                    var scan = await _sources.ScanAsync(root, cancellationToken).ConfigureAwait(true);
                    count = scan.Instances.Count;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    ErrorText = _localizer.Get("instanceDirectories.scanFailed", root.Path, exception.Message);
                }

                Directories.Add(new InstanceDirectoryRowViewModel(
                    root.Id,
                    root.Name,
                    root.Path,
                    count,
                    string.Equals(_workspace.Primary?.Id, root.Id, StringComparison.Ordinal),
                    RemoveText,
                    _localizer.Get("instanceDirectories.primaryHint"),
                    _localizer.Get("instanceDirectories.count", count)));
            }

            OnPropertyChanged(nameof(IsEmpty));
        }
        finally
        {
            IsBusy = false;
            _session?.SetBusy(false);
        }
    }

    [RelayCommand]
    private async Task AddDirectoryAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var folder = _fileDialogs.PickFolder(_localizer.Get("instanceDirectories.pickTitle"));
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        var trimmed = Path.TrimEndingDirectorySeparator(folder);
        var name = Path.GetFileName(trimmed);

        // 只接受名称为 .minecraft 的目录；不检查目录内部是否存在可识别实例。
        if (!string.Equals(name, ".minecraft", StringComparison.OrdinalIgnoreCase))
        {
            HasSelectionError = true;
            ErrorText = _localizer.Get("instanceDirectories.notMinecraft", folder);
            return;
        }

        if (Directories.Any(row => string.Equals(
                Path.TrimEndingDirectorySeparator(row.Path),
                trimmed,
                StringComparison.OrdinalIgnoreCase)))
        {
            HasSelectionError = true;
            ErrorText = _localizer.Get("instanceDirectories.duplicate", folder);
            return;
        }

        var result = await _workspace.RegisterExternalAsync(trimmed).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            HasSelectionError = true;
            ErrorText = result.Error?.Message ?? _localizer.Get("instanceDirectories.addFailed");
            return;
        }

        HasSelectionError = false;
        ErrorText = string.Empty;
        await LoadAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RemoveDirectoryAsync(InstanceDirectoryRowViewModel? row)
    {
        if (row is null || IsBusy)
        {
            return;
        }

        if (row.IsPrimary)
        {
            ErrorText = _localizer.Get("instanceDirectories.primaryRemove");
            return;
        }

        // 解绑与删除是两个不同操作，必须由用户明确选择（§4.4）。
        var choice = await _dialogs.ShowAsync(new DialogRequest
        {
            Title = _localizer.Get("instanceDirectories.removeTitle", row.DisplayName),
            Content = new MessageDialogViewModel(
                _localizer.Get("instanceDirectories.removeMessage", row.Path),
                _localizer.Get("instanceDirectories.unbind"),
                _localizer.Get("instanceDirectories.deleteInstances"),
                _localizer.Get("dialog.cancel"),
                DialogSeverity.Warning,
                isDestructive: true),
            InitialFocus = DialogInitialFocus.Cancel,
            Severity = DialogSeverity.Warning,
        }).ConfigureAwait(true);

        if (choice.IsPrimary)
        {
            var removed = await _workspace.RemoveRootAsync(row.RootId).ConfigureAwait(true);
            if (!removed.IsSuccess)
            {
                ErrorText = removed.Error?.Message ?? _localizer.Get("instanceDirectories.removeFailed");
                return;
            }

            ErrorText = string.Empty;
            await LoadAsync().ConfigureAwait(true);
            return;
        }

        if (choice.Outcome == DialogOutcome.Secondary && _deleteInstancesInDirectory is not null)
        {
            await _deleteInstancesInDirectory(row.Path).ConfigureAwait(true);
            await LoadAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private void Done() => _session?.TryClose(DialogOutcome.Primary);

    partial void OnErrorTextChanged(string value) => OnPropertyChanged(nameof(HasError));
}

/// <summary>“已经选择的目录”中的一行：实例名、完整路径、识别到的实例个数与移除入口。</summary>
public sealed record InstanceDirectoryRowViewModel(
    string RootId,
    string DisplayName,
    string Path,
    int InstanceCount,
    bool IsPrimary,
    string RemoveText,
    string PrimaryHint,
    string CountText);
