// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using Ecl.Core.Runtime;
using Ecl.Operations;
using Microsoft.Extensions.Logging;

namespace Ecl.App.Services.Lifecycle;

/// <summary>
/// 关闭协调的时限参数。默认值来自 F-12：任务取消后最多等 8 秒，关闭步骤上限 10 秒。
/// 测试注入更短的时限，避免为了验证“超时不阻塞”真的等 8 秒。
/// </summary>
public sealed record AppShutdownOptions
{
    /// <summary>请求取消活动任务后等待其进入终态的上限。</summary>
    public TimeSpan OperationDrainTimeout { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>轮询任务快照的间隔。</summary>
    public TimeSpan OperationPollInterval { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>整个“operations”停止步骤的时限（覆盖等待与轮询开销）。</summary>
    public TimeSpan StopStepTimeout { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// 关闭协调实现。
///
/// 安全边界：
/// - 只注册一个 <c>"operations"</c> 停止步骤：请求取消非终态任务并等待终态；
///   **不**注册游戏进程、LogHost 或其他模块的停止步骤——那些模块自己拥有生命周期，
///   在这里越权停止会把“关闭启动器”变成“杀掉正在运行的游戏”。
/// - 等待超时只记日志，不抛异常、不阻塞退出；未结束的任务保留在关闭报告里。
/// - 服务容器的释放失败只记日志：退出路径上的异常不能让用户卡在关闭界面。
/// </summary>
public sealed class AppShutdownCoordinator : IAppShutdownCoordinator
{
    private readonly IApplicationLifecycle _lifecycle;
    private readonly IOperationService _operations;
    private readonly IServiceProvider _services;
    private readonly ILogger<AppShutdownCoordinator> _logger;
    private readonly AppShutdownOptions _options;
    private readonly object _gate = new();
    private Task? _shutdownTask;

    public AppShutdownCoordinator(
        IApplicationLifecycle lifecycle,
        IOperationService operations,
        IServiceProvider services,
        ILogger<AppShutdownCoordinator> logger,
        AppShutdownOptions? options = null)
    {
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options ?? new AppShutdownOptions();

        try
        {
            _lifecycle.RegisterStopStep("operations", StopOperationsAsync, _options.StopStepTimeout);
        }
        catch (InvalidOperationException exception)
        {
            // 协调器可能在关闭已经开始的路径上被构造（例如出错处理里才解析到它）。
            // 这种情况下步骤无法再登记，但 ShutdownAsync 仍然可以驱动已有的关闭流程。
            _logger.LogWarning(exception, "关闭流程已开始，无法登记 operations 停止步骤。");
        }
    }

    public bool IsShuttingDown
    {
        get
        {
            lock (_gate)
            {
                return _shutdownTask is not null;
            }
        }
    }

    public Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return _shutdownTask ??= ShutdownCoreAsync(cancellationToken);
        }
    }

    private async Task ShutdownCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            var report = await _lifecycle.ShutdownAsync(cancellationToken).ConfigureAwait(false);
            if (!report.IsClean)
            {
                _logger.LogWarning(
                    "关闭完成但存在问题（{Count} 项）：{Issues}",
                    report.Issues.Count,
                    string.Join("；", report.Issues));
            }
        }
        catch (Exception exception)
        {
            // 关闭继续往下走：容器仍要释放，进程仍要退出。
            _logger.LogError(exception, "执行生命周期关闭流程失败。");
        }

        try
        {
            if (_services is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            }
            else if (_services is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "释放服务容器失败。");
        }
    }

    /// <summary>
    /// 取消所有非终态任务并等待其进入终态。取消请求只是“请求”：
    /// 不响应取消的提供者会被等到时限，然后按超时记录，绝不强杀线程或进程。
    /// </summary>
    private async Task StopOperationsAsync(CancellationToken cancellationToken)
    {
        var pending = _operations.Store.Snapshots.Where(snapshot => !snapshot.IsTerminal).ToArray();
        if (pending.Length == 0)
        {
            return;
        }

        foreach (var snapshot in pending)
        {
            try
            {
                if (!_operations.TryCancel(snapshot.Id))
                {
                    _logger.LogInformation(
                        "任务 {OperationId} 没有可用的取消控制，等待其自然结束。",
                        snapshot.Id);
                }
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "请求取消任务 {OperationId} 失败。", snapshot.Id);
            }
        }

        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < _options.OperationDrainTimeout)
        {
            var remaining = _operations.Store.Snapshots.Count(snapshot => !snapshot.IsTerminal);
            if (remaining == 0)
            {
                return;
            }

            await Task.Delay(_options.OperationPollInterval, cancellationToken).ConfigureAwait(false);
        }

        var stuck = _operations.Store.Snapshots.Count(snapshot => !snapshot.IsTerminal);
        _logger.LogWarning(
            "关闭等待超时：仍有 {Count} 个任务未结束（已等待 {Timeout}）。",
            stuck,
            _options.OperationDrainTimeout);
    }
}
