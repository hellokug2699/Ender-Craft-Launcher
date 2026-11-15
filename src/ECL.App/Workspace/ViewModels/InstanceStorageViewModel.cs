// SPDX-License-Identifier: GPL-3.0-or-later

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.App.Services;
using Ecl.Core.Localization;
using Ecl.Minecraft.Instances;
using Ecl.Minecraft.Storage;

namespace Ecl.App.Workspace.ViewModels;

/// <summary>
/// 基本信息「存储空间」卡片（`instance-workspace.md` §5）。
///
/// 使用与存储中心相同的分析服务与状态语义测量当前实例，未知/不可访问不当作 0；
/// 「管理存储空间」只负责跳转并定位实例，不在卡片内执行清理（ADR 0022）。
/// </summary>
public sealed partial class InstanceStorageViewModel : ObservableObject, IDisposable
{
    private readonly GameInstance _instance;
    private readonly Func<GameInstance, CancellationToken, Task<StorageUsage>> _measure;
    private readonly Action<string>? _manageStorage;
    private readonly ILocalizer _localizer;
    private CancellationTokenSource? _scanCancellation;
    private int _scanVersion;

    public InstanceStorageViewModel(
        GameInstance instance,
        Func<GameInstance, CancellationToken, Task<StorageUsage>> measure,
        Action<string>? manageStorage,
        ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(measure);
        ArgumentNullException.ThrowIfNull(localizer);

        _instance = instance;
        _measure = measure;
        _manageStorage = manageStorage;
        _localizer = localizer;
        _statusText = localizer.Get("instance.storage.notScanned");
        _sizeText = localizer.Get("instance.storage.unknownSize");
        _detailText = localizer.Get("instance.storage.detailUnavailable");
    }

    public string InstanceId => _instance.Id;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private bool _hasKnownSize;

    [ObservableProperty]
    private string _sizeText;

    [ObservableProperty]
    private string _statusText;

    [ObservableProperty]
    private string _detailText;

    public bool IsDisposed { get; private set; }

    /// <summary>测量当前实例占用；并发调用只保留最后一次结果。</summary>
    public async Task RefreshAsync()
    {
        if (IsDisposed)
        {
            return;
        }

        var version = ++_scanVersion;
        _scanCancellation?.Cancel();
        _scanCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _scanCancellation = cancellation;

        IsScanning = true;
        HasKnownSize = false;
        SizeText = _localizer.Get("instance.storage.unknownSize");
        StatusText = _localizer.Get("instance.storage.scanning");
        DetailText = _localizer.Get("instance.storage.detailUnavailable");

        try
        {
            var usage = await _measure(_instance, cancellation.Token).ConfigureAwait(true);
            if (IsDisposed || version != _scanVersion)
            {
                return;
            }

            Apply(usage);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // 页面关闭或重新扫描时取消，不写入结果。
        }
        catch (Exception)
        {
            if (IsDisposed || version != _scanVersion)
            {
                return;
            }

            HasKnownSize = false;
            SizeText = _localizer.Get("instance.storage.unknownSize");
            StatusText = _localizer.Get("instance.storage.failed");
            DetailText = _localizer.Get("instance.storage.detailUnavailable");
        }
        finally
        {
            if (!IsDisposed && version == _scanVersion)
            {
                IsScanning = false;
            }
        }
    }

    [RelayCommand]
    private void ManageStorage()
    {
        if (IsDisposed)
        {
            return;
        }

        _manageStorage?.Invoke(_instance.Id);
    }

    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        IsDisposed = true;
        _scanCancellation?.Cancel();
        _scanCancellation?.Dispose();
        _scanCancellation = null;
    }

    private void Apply(StorageUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);

        HasKnownSize = usage.Bytes.HasValue;
        SizeText = usage.Bytes is { } bytes
            ? StorageSizeFormatter.FormatBytes(bytes)
            : _localizer.Get("instance.storage.unknownSize");
        StatusText = usage.Status switch
        {
            StorageUsageStatus.Scanned => _localizer.Get("instance.storage.ready"),
            StorageUsageStatus.Estimated => _localizer.Get("instance.storage.estimated"),
            StorageUsageStatus.Overlapping => _localizer.Get("instance.storage.overlapping"),
            _ => _localizer.Get("instance.storage.unavailable"),
        };
        DetailText = usage.Bytes.HasValue
            ? _localizer.Get("instance.storage.detail", usage.FileCount)
            : _localizer.Get("instance.storage.detailUnavailable");
    }
}
