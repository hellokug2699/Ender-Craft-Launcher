// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using Ecl.App.Shell;
using Ecl.App.Workspace.ViewModels;
using Ecl.Core.Workspace;
using Ecl.Core.Workspace.Session;
using Microsoft.Extensions.Logging;

namespace Ecl.App.Shell.Lifecycle;

/// <summary>
/// 最小工作区会话快照（F-12 最小切片）。
///
/// 边界（有意不做的部分）：
/// - 只记录当前导航目的地与打开过的实例工作区身份，不恢复内容页签、Split、Addition 布局；
/// - 不记录滚动位置、筛选条件或未保存的编辑状态：这些属于页面自身，恢复它们会伪造“之前的状态”；
/// - 快照只是便利数据：写入失败只记日志，绝不影响导航或退出。
///
/// 线程契约：只订阅 UI 线程上的 Shell 属性/集合变更，因此读写都在 UI 线程；
/// <see cref="Save"/> 在退出前也会由宿主显式调用一次。
/// </summary>
public sealed class WorkspaceSessionTracker : IDisposable
{
    private readonly ShellViewModel _shell;
    private readonly IWorkspaceSessionStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WorkspaceSessionTracker> _logger;
    private bool _captureEnabled;
    private bool _disposed;

    public WorkspaceSessionTracker(
        ShellViewModel shell,
        IWorkspaceSessionStore store,
        TimeProvider timeProvider,
        ILogger<WorkspaceSessionTracker> logger)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _shell.PropertyChanged += OnShellPropertyChanged;
        _shell.Workspaces.CollectionChanged += OnWorkspacesChanged;
    }

    /// <summary>
    /// 启动恢复询问结束后才允许写快照。Shell 构造时会先打开默认目的地；
    /// 若那时就写盘，会覆盖“上次退出”的快照，让恢复询问永远看不到旧会话。
    /// </summary>
    public void EnableCapture()
    {
        if (_captureEnabled || _disposed)
        {
            return;
        }

        _captureEnabled = true;
        Save();
    }

    public void Save()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _store.Save(Capture());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 会话快照是可再生的便利数据：写不进去不能阻断退出或导航。
            _logger.LogWarning(exception, "保存工作区会话快照失败；不影响启动与退出。");
        }
    }

    /// <summary>按当前 Shell 状态构造快照；不修改任何页面或工作区。</summary>
    public WorkspaceSessionSnapshot Capture()
    {
        var open = new List<WorkspaceSessionEntry>();
        var activeIndex = 0;
        foreach (var tab in _shell.Workspaces)
        {
            if (!IsInstanceWorkspace(tab, out var instanceId))
            {
                continue;
            }

            if (ReferenceEquals(tab, _shell.ActiveWorkspace))
            {
                activeIndex = open.Count;
            }

            open.Add(new WorkspaceSessionEntry(ToolId: null, InstanceId: instanceId, Destination: null));
        }

        return new WorkspaceSessionSnapshot(
            WorkspaceSessionSnapshot.CurrentSchemaVersion,
            open,
            activeIndex,
            _shell.SelectedNavigationItem?.Key,
            _timeProvider.GetUtcNow());
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shell.PropertyChanged -= OnShellPropertyChanged;
        _shell.Workspaces.CollectionChanged -= OnWorkspacesChanged;
    }

    /// <summary>只把“实例工作区”当作可恢复条目；固定目的地由 NavigationDestination 表达。</summary>
    private static bool IsInstanceWorkspace(WorkspaceTabViewModel tab, out string instanceId)
    {
        instanceId = string.Empty;
        if (!string.Equals(tab.ToolId, "instance", StringComparison.Ordinal))
        {
            return false;
        }

        if (!WorkspaceTargetSerializer.TryParse(tab.TargetResource, out var target) ||
            target is not { Kind: WorkspaceTargetKind.Instance, StableInstanceId: { } stableId } ||
            string.IsNullOrWhiteSpace(stableId))
        {
            return false;
        }

        instanceId = stableId;
        return true;
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 只关心“当前在哪个目的地/工作区”，进度刷新等属性变化不触发写盘。
        if (e.PropertyName is nameof(ShellViewModel.ActiveWorkspace) or nameof(ShellViewModel.SelectedNavigationItem))
        {
            SaveIfCapturing();
        }
    }

    private void OnWorkspacesChanged(object? sender, NotifyCollectionChangedEventArgs e) => SaveIfCapturing();

    private void SaveIfCapturing()
    {
        if (_captureEnabled)
        {
            Save();
        }
    }
}
