// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using Ecl.Core;
using Ecl.Core.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ecl.Minecraft.Engine.Lifecycle;

/// <summary>实例生命周期租约；释放顺序为先文件锁后进程内信号量。</summary>
public interface IInstanceLifecycleLease : IAsyncDisposable
{
    string InstanceId { get; }
}

/// <summary>
/// 实例生命周期锁（F-03 §5.2/§8.7）：同一实例同一时刻只允许一个破坏性生命周期操作。
/// 进程内用 <see cref="SemaphoreSlim"/>，跨进程用 <c>&lt;Root&gt;/locks/instance-engine/&lt;id&gt;.lifecycle.lock</c>。
/// 有界等待，失败返回 <c>instance.lifecycle.busy</c>，不排队执行破坏性步骤。
/// </summary>
public interface IInstanceLifecycleLock
{
    Task<Result<IInstanceLifecycleLease>> AcquireAsync(
        string instanceId,
        CancellationToken cancellationToken = default);
}

public sealed class InstanceLifecycleLock : IInstanceLifecycleLock
{
    /// <summary>有界等待；破坏性操作不允许无限排队。</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _semaphores =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly IAppPaths _paths;
    private readonly ILogger<InstanceLifecycleLock> _logger;
    private readonly TimeSpan _timeout;

    public InstanceLifecycleLock(
        IAppPaths paths,
        ILogger<InstanceLifecycleLock>? logger = null,
        TimeSpan? timeout = null)
    {
        _paths = paths;
        _logger = logger ?? NullLogger<InstanceLifecycleLock>.Instance;
        _timeout = timeout ?? DefaultTimeout;
    }

    public async Task<Result<IInstanceLifecycleLease>> AcquireAsync(
        string instanceId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return Result<IInstanceLifecycleLease>.Fail("instance.invalid_id", "实例 ID 不合法。");
        }

        var semaphore = _semaphores.GetOrAdd(instanceId, _ => new SemaphoreSlim(1, 1));
        var entered = false;
        try
        {
            entered = await semaphore.WaitAsync(_timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }

        if (!entered)
        {
            return Result<IInstanceLifecycleLease>.Fail(
                "instance.lifecycle.busy",
                "同一实例已有生命周期操作在进行，请稍后重试。");
        }

        CrossProcessFileLock? fileLock = null;
        try
        {
            fileLock = CrossProcessFileLock.TryAcquire(
                _paths.Root,
                $"{instanceId}.lifecycle.lock",
                _timeout,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            semaphore.Release();
            throw;
        }

        if (fileLock is null)
        {
            semaphore.Release();
            _logger.LogWarning("无法获取实例 {InstanceId} 的跨进程生命周期锁。", instanceId);
            return Result<IInstanceLifecycleLease>.Fail(
                "instance.lifecycle.busy",
                "另一个 ECL 进程正在对该实例执行生命周期操作，请稍后重试。");
        }

        return Result<IInstanceLifecycleLease>.Ok(new Lease(instanceId, fileLock, semaphore));
    }

    private sealed class Lease(
        string instanceId,
        CrossProcessFileLock fileLock,
        SemaphoreSlim semaphore) : IInstanceLifecycleLease
    {
        private int _disposed;

        public string InstanceId { get; } = instanceId;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                fileLock.Dispose();
                semaphore.Release();
            }

            return ValueTask.CompletedTask;
        }
    }
}
