// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.App.Services;
using Ecl.Configuration.Settings;
using Ecl.Core;
using Ecl.Core.Localization;
using Ecl.Java;
using Microsoft.Extensions.Logging;

namespace Ecl.App.ViewModels.Settings;

/// <summary>
/// 设置中心「Java 管理」页（页面规格 java-management.md）。
///
/// - 全局候选优先级，而不是为所有实例固定指定一个 Java；
/// - 首次进入页面才做一次有界发现，同一运行周期内再次进入复用结果；
/// - 紧凑单行表格：拖动标志 / 主版本 / 名称 / 路径 / 操作；
/// - “移除”只解除 ECL 登记/候选关系，不删除文件；“卸载”只针对 ECL 托管且无实例固定引用、
///   无活动会话的运行时；
/// - 未配置用户优先级时不写入虚构顺序；只有用户实际调整顺序后才写 <c>java.priority</c>。
/// </summary>
public sealed partial class SettingsJavaPageViewModel : ObservableObject, ISettingsPage, IDisposable
{
    private const int ValidationConcurrency = 3;

    private readonly IJavaLocator _locator;
    private readonly IJavaRuntimeRegistry _registry;
    private readonly IJavaRuntimeValidator _validator;
    private readonly IJavaRuntimeUninstallService _uninstall;
    private readonly ISettingsService _settings;
    private readonly IDialogService _dialogs;
    private readonly ILocalizer _localizer;
    private readonly ILogger<SettingsJavaPageViewModel> _logger;
    private readonly string _runtimesDirectory;
    private readonly CancellationTokenSource _lifetime = new();

    private bool _activated;
    private bool _disposed;
    private bool _hasExplicitPriority;

    public SettingsJavaPageViewModel(
        IJavaLocator locator,
        IJavaRuntimeRegistry registry,
        IJavaRuntimeValidator validator,
        IJavaRuntimeUninstallService uninstall,
        ISettingsService settings,
        IDialogService dialogs,
        IAppPaths paths,
        ILocalizer localizer,
        ILogger<SettingsJavaPageViewModel> logger)
    {
        _locator = locator;
        _registry = registry;
        _validator = validator;
        _uninstall = uninstall;
        _settings = settings;
        _dialogs = dialogs;
        _localizer = localizer;
        _logger = logger;
        _runtimesDirectory = paths.RuntimesDirectory;

        var defaultPath = new OptionEditorViewModel(
            settings.GetDefinition("java.defaultPath"),
            settings,
            localizer,
            instanceId: null);
        var provider = new OptionEditorViewModel(
            settings.GetDefinition("java.runtimeProvider"),
            settings,
            localizer,
            instanceId: null);
        Options = [defaultPath, provider];
        foreach (var option in Options)
        {
            option.SetVisible(settings.IsVisible(option.Definition));
        }

        SourceGroup = new SettingsOptionGroupViewModel(
            localizer.Get("settings.page.java.group.sources"),
            Options);
        SearchEntries = Options
            .Select(option => new SettingsSearchEntry(
                SettingsPageKeys.Java,
                Title,
                option.Key,
                option.DisplayName,
                option.Description))
            .ToArray();

        Runtimes.CollectionChanged += OnRuntimesChanged;
    }

    public string Key => SettingsPageKeys.Java;

    public string Title => _localizer.Get("settings.nav.java");

    public string Subtitle => _localizer.Get("settings.page.java.subtitle");

    public IReadOnlyList<SettingsSearchEntry> SearchEntries { get; }

    /// <summary>“Java 目录 / 路径”与“运行时来源”仍由元数据驱动的通用设置行呈现。</summary>
    public IReadOnlyList<OptionEditorViewModel> Options { get; }

    public SettingsOptionGroupViewModel SourceGroup { get; }

    public ObservableCollection<JavaManagementRowViewModel> Runtimes { get; } = [];

    public bool CanResetDefaults => false;

    [ObservableProperty]
    private JavaManagementRowViewModel? _selectedRuntime;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public bool HasRuntimes => Runtimes.Count > 0;

    public string CountText => _localizer.Get("java.page.count", Runtimes.Count);

    public string EmptyMessage => _localizer.Get("java.page.empty");

    public bool CanValidateSelected => SelectedRuntime is not null && !IsBusy;

    /// <summary>首次进入页面或重新进入时调用；只有第一次会触发发现/验证。</summary>
    public void Activate()
    {
        if (_activated || _disposed)
        {
            return;
        }

        _activated = true;
        _ = ActivateAsync();
    }

    public async Task ActivateAsync()
    {
        if (_disposed)
        {
            return;
        }

        if (_activated && Runtimes.Count > 0)
        {
            return;
        }

        _activated = true;
        await ScanAsync().ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task RefreshAsync() => ScanAsync();

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private async Task AddJavaAsync()
    {
        var path = _dialogs.PickOpenFile(
            _localizer.Get("java.manager.importTitle"),
            OperatingSystem.IsWindows()
                ? "Java executable|java.exe|Executable|*.exe|All files|*.*"
                : "Java executable|java|All files|*");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var runtime = _locator.FromPath(path);
        if (runtime is null)
        {
            _dialogs.ShowError(Title, _localizer.Get("java.manager.invalidRuntime"));
            return;
        }

        var existing = Runtimes.FirstOrDefault(row =>
            JavaRuntimePath.Same(row.ExecutablePath, runtime.ExecutablePath));
        if (existing is not null)
        {
            SelectedRuntime = existing;
            StatusMessage = _localizer.Get("java.page.exists");
            return;
        }

        IsBusy = true;
        try
        {
            var validation = await _validator
                .ValidateAsync(runtime, _lifetime.Token)
                .ConfigureAwait(true);
            if (_disposed)
            {
                return;
            }

            if (validation.IsUnsupported32Bit)
            {
                // 32 位 Java 不能用于 Minecraft 启动：拒绝登记，并说明真实原因。
                _dialogs.ShowError(Title, _localizer.Get("java.page.importRejected32"));
                return;
            }

            if (!_registry.RegisterImported(runtime.ExecutablePath))
            {
                StatusMessage = _localizer.Get("java.page.addFailed");
                return;
            }

            var origin = JavaRuntimeCatalog.ResolveOrigin(
                runtime,
                [JavaRuntimePath.Canonicalize(runtime.ExecutablePath)],
                _runtimesDirectory);
            var row = new JavaManagementRowViewModel(runtime, origin, _localizer);
            row.ApplyValidation(validation);
            Runtimes.Add(row);
            if (_hasExplicitPriority)
            {
                SavePriority();
            }

            SelectedRuntime = row;
            StatusMessage = _localizer.Get("java.manager.imported", row.Name);
            await RefreshUninstallAvailabilityAsync(_lifetime.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // 页面已释放：不写状态。
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanValidateSelected))]
    private async Task ValidateSelectedAsync()
    {
        if (SelectedRuntime is not { } row)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await ValidateRowAsync(row, _lifetime.Token).ConfigureAwait(true);
            if (_disposed)
            {
                return;
            }

            StatusMessage = row.State == JavaRuntimeValidationState.Available
                ? _localizer.Get("java.page.validate.ok", row.Name)
                : _localizer.Get("java.page.validate.failed", row.Name, row.StateText);
            await RefreshUninstallAvailabilityAsync(_lifetime.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // 页面已释放。
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private void Remove(JavaManagementRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        var accepted = _dialogs.Confirm(
            _localizer.Get("java.page.remove.title"),
            _localizer.Get("java.page.remove.message", row.Name, row.Path),
            acknowledgementToken: null);
        if (!accepted)
        {
            return;
        }

        if (!_registry.MarkRemoved(row.ExecutablePath))
        {
            StatusMessage = _localizer.Get("java.page.remove.failed");
            return;
        }

        Runtimes.Remove(row);
        if (_hasExplicitPriority)
        {
            SavePriority();
        }

        if (ReferenceEquals(SelectedRuntime, row))
        {
            SelectedRuntime = null;
        }

        StatusMessage = _localizer.Get("java.page.remove.done", row.Name);
    }

    [RelayCommand(CanExecute = nameof(CanUninstallRow))]
    private async Task UninstallAsync(JavaManagementRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        if (!row.IsManaged)
        {
            StatusMessage = _localizer.Get(
                "java.page.uninstall.blocked",
                _localizer.Get("java.page.blocker.notManaged"));
            return;
        }

        IsBusy = true;
        try
        {
            var plan = await _uninstall
                .CreatePlanAsync(row.Runtime, _lifetime.Token)
                .ConfigureAwait(true);
            if (_disposed)
            {
                return;
            }

            row.ApplyUninstallPlan(plan);
            if (!plan.CanUninstall)
            {
                StatusMessage = _localizer.Get("java.page.uninstall.blocked", row.BlockerSummary);
                return;
            }

            var accepted = _dialogs.Confirm(
                _localizer.Get("java.page.uninstall.title"),
                _localizer.Get(
                    "java.page.uninstall.message",
                    row.Name,
                    row.VersionText,
                    row.Path),
                acknowledgementToken: null);
            if (!accepted)
            {
                return;
            }

            var result = await _uninstall.UninstallAsync(row.Runtime, _lifetime.Token).ConfigureAwait(true);
            if (_disposed)
            {
                return;
            }

            if (!result.Succeeded)
            {
                // 卸载失败或部分完成时保留该行并如实说明；重新验证以反映磁盘上的真实状态，
                // 不伪装成已卸载。
                await ValidateRowAsync(row, _lifetime.Token).ConfigureAwait(true);
                if (_disposed)
                {
                    return;
                }

                StatusMessage = _localizer.Get(
                    "java.page.uninstall.failed",
                    result.ErrorDetail ?? string.Empty);
                return;
            }

            _registry.Forget(row.ExecutablePath);
            Runtimes.Remove(row);
            if (_hasExplicitPriority)
            {
                SavePriority();
            }

            if (ReferenceEquals(SelectedRuntime, row))
            {
                SelectedRuntime = null;
            }

            StatusMessage = _localizer.Get("java.page.uninstall.done", row.Name);
        }
        catch (OperationCanceledException)
        {
            // 页面已释放：删除由领域服务独立完成，UI 不回写。
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "卸载 Java 运行时失败。");
            StatusMessage = _localizer.Get("java.page.uninstall.failed", exception.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 拖动排序入口（视图在 Drop 时调用）。顺序表达全局候选优先级，
    /// 写盘失败时恢复到拖动前的顺序并说明原因。
    /// </summary>
    public bool MoveRow(JavaManagementRowViewModel? row, int targetIndex)
    {
        if (row is null || IsBusy)
        {
            return false;
        }

        var index = Runtimes.IndexOf(row);
        if (index < 0)
        {
            return false;
        }

        var target = Math.Clamp(targetIndex, 0, Runtimes.Count);
        if (target == index || target == index + 1)
        {
            return false;
        }

        var previous = Runtimes.ToArray();
        Runtimes.Move(index, target > index ? target - 1 : target);
        if (SavePriority())
        {
            StatusMessage = _localizer.Get("java.page.reordered");
            return true;
        }

        Runtimes.Clear();
        foreach (var item in previous)
        {
            Runtimes.Add(item);
        }

        return false;
    }

    public bool Highlight(string optionKey)
    {
        var target = Options.FirstOrDefault(option =>
            string.Equals(option.Key, optionKey, StringComparison.Ordinal));
        if (target is null)
        {
            return false;
        }

        foreach (var option in Options)
        {
            option.IsHighlighted = ReferenceEquals(option, target);
        }

        StatusMessage = _localizer.Get("settings.search.located", target.DisplayName);
        return true;
    }

    public void ResetToDefaults()
    {
        // Java 管理页的列表与登记不是元数据默认值，不提供会把用户登记误清空的“恢复默认”。
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Runtimes.CollectionChanged -= OnRuntimesChanged;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private bool CanRefresh() => !IsBusy;

    private bool CanAdd() => !IsBusy;

    /// <summary>
    /// 已登记但暂时不可读取的运行时仍要显示为“路径失效”，不能因为一次扫描解析不到
    /// 就无声地从列表消失（登记不因扫描结果被删除）。
    /// </summary>
    private JavaRuntime? ResolveRegisteredRuntime(string executablePath)
    {
        var resolved = _locator.FromPath(executablePath);
        if (resolved is not null)
        {
            return resolved;
        }

        try
        {
            var executable = Path.GetFullPath(executablePath.Trim().Trim('"'));
            var binDirectory = Path.GetDirectoryName(executable);
            var home = binDirectory is null ? null : Path.GetDirectoryName(binDirectory);
            return home is null
                ? null
                : new JavaRuntime(
                    home,
                    executable,
                    Vendor: null,
                    Architecture: null,
                    Version: null,
                    Source: JavaRuntimeSource.UserImported);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private bool CanRemove(JavaManagementRowViewModel? row) => row is not null && !IsBusy;

    private bool CanUninstallRow(JavaManagementRowViewModel? row) => row is { CanUninstall: true } && !IsBusy;

    private async Task ScanAsync()
    {
        if (IsBusy || _disposed)
        {
            return;
        }

        IsBusy = true;
        IsScanning = true;
        StatusMessage = _localizer.Get("java.manager.scanning");
        try
        {
            // 有界发现：只使用 Locator 的既定范围（JAVA_HOME/PATH/系统位置/搜索路径），
            // 用户显式“重新扫描”才重新发现；打开 ECL 或普通设置中心不会触发。
            var discovered = await _locator.RefreshAsync(_lifetime.Token).ConfigureAwait(true);
            if (_disposed)
            {
                return;
            }

            var imported = _registry.ImportedPaths
                .Select(ResolveRegisteredRuntime)
                .Where(runtime => runtime is not null)
                .Select(runtime => runtime!)
                .ToArray();

            var explicitPriority = JavaSearchPath.Split(
                _settings.Get<string>(JavaPriorityOrdering.SettingsKey));
            var legacyConfigured = JavaSearchPath.Split(_settings.Get<string>("java.defaultPath"));
            _hasExplicitPriority = explicitPriority.Count > 0;

            var entries = JavaRuntimeCatalog.Compose(
                discovered,
                imported,
                _registry.RemovedPaths,
                JavaPriorityOrdering.SelectSeed(explicitPriority, legacyConfigured),
                _runtimesDirectory);

            RebuildRows(entries);
            StatusMessage = _localizer.Get("java.manager.count", Runtimes.Count);

            await ValidateAllAsync(_lifetime.Token).ConfigureAwait(true);
            if (_disposed)
            {
                return;
            }

            await RefreshUninstallAvailabilityAsync(_lifetime.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // 页面已释放：停止后续回写。
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "扫描 Java 运行时失败。");
            StatusMessage = _localizer.Get("java.page.scanFail", exception.Message);
        }
        finally
        {
            IsBusy = false;
            IsScanning = false;
        }
    }

    private void RebuildRows(IReadOnlyList<JavaRuntimeCatalogEntry> entries)
    {
        var selectedPath = SelectedRuntime?.ExecutablePath;
        Runtimes.Clear();
        foreach (var entry in entries)
        {
            Runtimes.Add(new JavaManagementRowViewModel(entry.Runtime, entry.Origin, _localizer));
        }

        SelectedRuntime = selectedPath is null
            ? null
            : Runtimes.FirstOrDefault(row =>
                string.Equals(
                    JavaRuntimePath.Canonicalize(row.ExecutablePath),
                    JavaRuntimePath.Canonicalize(selectedPath),
                    StringComparison.Ordinal));
    }

    private async Task ValidateAllAsync(CancellationToken cancellationToken)
    {
        var rows = Runtimes.ToArray();
        if (rows.Length == 0)
        {
            return;
        }

        using var gate = new SemaphoreSlim(ValidationConcurrency);
        var tasks = rows.Select(async row =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(true);
            try
            {
                await ValidateRowAsync(row, cancellationToken).ConfigureAwait(true);
            }
            finally
            {
                gate.Release();
            }
        }).ToArray();

        await Task.WhenAll(tasks).ConfigureAwait(true);
    }

    private async Task ValidateRowAsync(
        JavaManagementRowViewModel row,
        CancellationToken cancellationToken)
    {
        row.MarkValidating();
        try
        {
            var validation = await _validator
                .ValidateAsync(row.Runtime, cancellationToken)
                .ConfigureAwait(true);
            if (_disposed)
            {
                return;
            }

            row.ApplyValidation(validation);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (_disposed)
            {
                return;
            }

            // 一个候选验证失败不影响其它候选；失败原因来自实际异常。
            row.ApplyValidation(new JavaRuntimeValidation(
                JavaRuntimeValidationState.Failed,
                null,
                null,
                null,
                JavaRuntimeValidationFailure.NotStarted,
                exception.Message,
                TimeSpan.Zero));
        }
    }

    /// <summary>
    /// 卸载可用性只在受管运行时上查询引用关系（外部 Java 永远不可卸载，无需探测）。
    /// 探测失败按“无法确认是否仍在使用”阻断，不冒险删除。
    /// </summary>
    private async Task RefreshUninstallAvailabilityAsync(CancellationToken cancellationToken)
    {
        foreach (var row in Runtimes.ToArray())
        {
            if (_disposed)
            {
                return;
            }

            if (!row.IsManaged)
            {
                row.ApplyUninstallPlan(new JavaUninstallPlan(
                    row.Runtime,
                    IsManaged: false,
                    row.HomePath,
                    [new JavaUninstallBlocker(JavaUninstallBlockerKind.NotManaged, _runtimesDirectory)]));
                continue;
            }

            try
            {
                var plan = await _uninstall
                    .CreatePlanAsync(row.Runtime, cancellationToken)
                    .ConfigureAwait(true);
                if (_disposed)
                {
                    return;
                }

                row.ApplyUninstallPlan(plan);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "无法确认 Java 运行时是否可卸载。");
                row.ApplyUninstallPlan(new JavaUninstallPlan(
                    row.Runtime,
                    IsManaged: true,
                    row.HomePath,
                    [new JavaUninstallBlocker(JavaUninstallBlockerKind.UsageUnknown, exception.Message)]));
            }
        }

        RaiseCommandsCanExecuteChanged();
    }

    private bool SavePriority()
    {
        var value = string.Join(';', Runtimes.Select(row => row.ExecutablePath));
        if (!_settings.TrySetRaw(JavaPriorityOrdering.SettingsKey, value, null, out var error))
        {
            StatusMessage = _localizer.Get("java.page.reorderFailed", error ?? string.Empty);
            return false;
        }

        _hasExplicitPriority = true;
        return true;
    }

    private void RaiseCommandsCanExecuteChanged()
    {
        RefreshCommand.NotifyCanExecuteChanged();
        AddJavaCommand.NotifyCanExecuteChanged();
        ValidateSelectedCommand.NotifyCanExecuteChanged();
        RemoveCommand.NotifyCanExecuteChanged();
        UninstallCommand.NotifyCanExecuteChanged();
    }

    private void OnRuntimesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasRuntimes));
        OnPropertyChanged(nameof(CountText));
        RaiseCommandsCanExecuteChanged();
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanValidateSelected));
        RaiseCommandsCanExecuteChanged();
    }

    partial void OnSelectedRuntimeChanged(JavaManagementRowViewModel? value)
    {
        OnPropertyChanged(nameof(CanValidateSelected));
        ValidateSelectedCommand.NotifyCanExecuteChanged();
    }
}
