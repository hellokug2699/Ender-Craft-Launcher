// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using Ecl.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ecl.Minecraft.Engine.Transactions;

/// <summary>
/// 实例写门（F-04 S1）。同一实例的写互斥；读租约可并发，但与写门互斥。
/// 进程内使用异步读写门，跨进程使用锁文件：写者独占打开，读者共享打开。
/// </summary>
public interface IInstanceWriteGate
{
    Task<Result<InstanceWriteLease>> AcquireAsync(
        string instanceId,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);

    Task<Result<InstanceReadLease>> AcquireReadAsync(
        string instanceId,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);

    /// <summary>只读取诊断状态，不获取锁。跨进程占用无法在不阻塞的情况下完整判断。</summary>
    bool IsWriteHeld(string instanceId);

    int ActiveReaderCount(string instanceId);
}

public sealed class InstanceWriteLease : IAsyncDisposable
{
    private readonly InstanceWriteGate _gate;
    private readonly string _instanceId;
    private readonly FileStream? _processLock;
    private int _disposed;

    internal InstanceWriteLease(InstanceWriteGate gate, string instanceId, FileStream? processLock)
    {
        _gate = gate;
        _instanceId = instanceId;
        _processLock = processLock;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _processLock?.Dispose();
        }
        finally
        {
            _gate.ReleaseWrite(_instanceId);
        }

        await ValueTask.CompletedTask.ConfigureAwait(false);
    }
}

public sealed class InstanceReadLease : IAsyncDisposable
{
    private readonly InstanceWriteGate _gate;
    private readonly string _instanceId;
    private readonly FileStream? _processLock;
    private int _disposed;

    internal InstanceReadLease(InstanceWriteGate gate, string instanceId, FileStream? processLock)
    {
        _gate = gate;
        _instanceId = instanceId;
        _processLock = processLock;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _processLock?.Dispose();
        }
        finally
        {
            _gate.ReleaseRead(_instanceId);
        }

        await ValueTask.CompletedTask.ConfigureAwait(false);
    }
}

/// <summary>
/// 实例级异步读写门 + 跨进程锁文件。锁文件不删除：句柄关闭即释放，删除会引入竞态。
/// </summary>
public sealed class InstanceWriteGate : IInstanceWriteGate
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    private readonly IAppPaths _paths;
    private readonly ILogger<InstanceWriteGate> _logger;
    private readonly ConcurrentDictionary<string, GateState> _states = new(StringComparer.Ordinal);
    private readonly bool _crossProcessSupported;
    private int _platformWarningLogged;

    public InstanceWriteGate(IAppPaths paths, ILogger<InstanceWriteGate>? logger = null)
    {
        _paths = paths;
        _logger = logger ?? NullLogger<InstanceWriteGate>.Instance;
        _crossProcessSupported = OperatingSystem.IsWindows();
    }

    public async Task<Result<InstanceWriteLease>> AcquireAsync(
        string instanceId,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (!AppPaths.IsValidInstanceId(instanceId))
        {
            return Result<InstanceWriteLease>.Fail("instance.invalid_id", "实例 ID 不合法。");
        }

        var wait = timeout ?? DefaultTimeout;
        if (wait <= TimeSpan.Zero)
        {
            return Result<InstanceWriteLease>.Fail("instance.write_busy", "实例正在被其他操作写入。");
        }

        var state = _states.GetOrAdd(instanceId, _ => new GateState());
        var granted = await state.AcquireWriteAsync(wait, cancellationToken).ConfigureAwait(false);
        if (!granted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Result<InstanceWriteLease>.Fail("instance.write_busy", "实例正在被其他操作写入，请稍后重试。");
        }

        FileStream? processLock = null;
        if (_crossProcessSupported)
        {
            processLock = await TryOpenLockFileAsync(
                GetLockPath(instanceId),
                FileAccess.ReadWrite,
                FileShare.None,
                wait,
                cancellationToken,
                writeDiagnostics: true).ConfigureAwait(false);
            if (processLock is null)
            {
                state.ReleaseWrite();
                return Result<InstanceWriteLease>.Fail(
                    "instance.write_busy",
                    "另一个 ECL 进程正在写入该实例，请稍后重试。");
            }
        }
        else
        {
            WarnCrossProcessUnsupported();
        }

        return Result<InstanceWriteLease>.Ok(new InstanceWriteLease(this, instanceId, processLock));
    }

    public async Task<Result<InstanceReadLease>> AcquireReadAsync(
        string instanceId,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (!AppPaths.IsValidInstanceId(instanceId))
        {
            return Result<InstanceReadLease>.Fail("instance.invalid_id", "实例 ID 不合法。");
        }

        var wait = timeout ?? DefaultTimeout;
        if (wait <= TimeSpan.Zero)
        {
            return Result<InstanceReadLease>.Fail("instance.read_busy", "实例正在变更，暂时无法读取稳定状态。");
        }

        var state = _states.GetOrAdd(instanceId, _ => new GateState());
        var granted = await state.AcquireReadAsync(wait, cancellationToken).ConfigureAwait(false);
        if (!granted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Result<InstanceReadLease>.Fail(
                "instance.read_busy",
                "实例正在变更，暂时无法读取稳定状态。",
                "Changing");
        }

        FileStream? processLock = null;
        if (_crossProcessSupported)
        {
            processLock = await TryOpenLockFileAsync(
                GetLockPath(instanceId),
                FileAccess.Read,
                FileShare.Read,
                wait,
                cancellationToken,
                writeDiagnostics: false).ConfigureAwait(false);
            if (processLock is null)
            {
                state.ReleaseRead();
                return Result<InstanceReadLease>.Fail(
                    "instance.read_busy",
                    "另一个 ECL 进程正在写入该实例，暂时无法读取稳定状态。",
                    "Changing");
            }
        }
        else
        {
            WarnCrossProcessUnsupported();
        }

        return Result<InstanceReadLease>.Ok(new InstanceReadLease(this, instanceId, processLock));
    }

    public bool IsWriteHeld(string instanceId) =>
        _states.TryGetValue(instanceId, out var state) && state.IsWriteHeld;

    public int ActiveReaderCount(string instanceId) =>
        _states.TryGetValue(instanceId, out var state) ? state.ActiveReaders : 0;

    internal void ReleaseWrite(string instanceId)
    {
        if (_states.TryGetValue(instanceId, out var state))
        {
            state.ReleaseWrite();
        }
    }

    internal void ReleaseRead(string instanceId)
    {
        if (_states.TryGetValue(instanceId, out var state))
        {
            state.ReleaseRead();
        }
    }

    private string GetLockPath(string instanceId) =>
        Path.Combine(_paths.Root, "locks", "instance-engine", $"{instanceId}.gate");

    private void WarnCrossProcessUnsupported()
    {
        if (Interlocked.Exchange(ref _platformWarningLogged, 1) != 0)
        {
            return;
        }

        _logger.LogWarning("当前平台未启用跨进程实例写门；仅保证进程内串行化。");
    }

    private static async Task<FileStream?> TryOpenLockFileAsync(
        string path,
        FileAccess access,
        FileShare share,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        bool writeDiagnostics)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var deadline = DateTimeOffset.UtcNow + timeout;
        Exception? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(path, FileMode.OpenOrCreate, access, share, 1, FileOptions.None);
                if (writeDiagnostics)
                {
                    try
                    {
                        stream.SetLength(0);
                        var payload = System.Text.Encoding.UTF8.GetBytes(
                            $"pid={Environment.ProcessId};started={DateTimeOffset.UtcNow:O}");
                        stream.Write(payload, 0, payload.Length);
                        stream.Flush(flushToDisk: true);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        // 诊断元数据失败不影响锁本身。
                    }
                }

                return stream;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                last = exception;
            }

            try
            {
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }

        _ = last;
        return null;
    }

    /// <summary>每个实例的异步读写门。写者优先，避免写者被连续读者饿死。</summary>
    private sealed class GateState
    {
        private readonly object _sync = new();
        private readonly LinkedList<Waiter> _readers = new();
        private readonly LinkedList<Waiter> _writers = new();
        private int _activeReaders;
        private bool _writerActive;

        public bool IsWriteHeld
        {
            get
            {
                lock (_sync)
                {
                    return _writerActive || _writers.Count > 0;
                }
            }
        }

        public int ActiveReaders
        {
            get
            {
                lock (_sync)
                {
                    return _activeReaders;
                }
            }
        }

        public async Task<bool> AcquireWriteAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            var waiter = new Waiter();
            lock (_sync)
            {
                if (!_writerActive && _activeReaders == 0 && _writers.Count == 0)
                {
                    _writerActive = true;
                    waiter.Granted = true;
                    return true;
                }

                _writers.AddLast(waiter);
            }

            return await WaitAsync(waiter, _writers, timeout, cancellationToken).ConfigureAwait(false);
        }

        public async Task<bool> AcquireReadAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            var waiter = new Waiter();
            lock (_sync)
            {
                // 写者优先：只要有写者等待，新读者排队，避免写者饿死。
                if (!_writerActive && _writers.Count == 0)
                {
                    _activeReaders++;
                    waiter.Granted = true;
                    return true;
                }

                _readers.AddLast(waiter);
            }

            return await WaitAsync(waiter, _readers, timeout, cancellationToken).ConfigureAwait(false);
        }

        public void ReleaseWrite()
        {
            lock (_sync)
            {
                if (!_writerActive)
                {
                    return;
                }

                _writerActive = false;
                if (_writers.Count > 0)
                {
                    GrantNextWriterLocked();
                }
                else
                {
                    GrantReadersLocked();
                }
            }
        }

        public void ReleaseRead()
        {
            lock (_sync)
            {
                if (_activeReaders > 0)
                {
                    _activeReaders--;
                }

                if (_activeReaders == 0 && !_writerActive && _writers.Count > 0)
                {
                    GrantNextWriterLocked();
                }
            }
        }

        private async Task<bool> WaitAsync(
            Waiter waiter,
            LinkedList<Waiter> queue,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var delay = Task.Delay(timeout, timeoutCts.Token);
            var completed = await Task.WhenAny(waiter.Completion.Task, delay).ConfigureAwait(false);
            if (completed == waiter.Completion.Task)
            {
                timeoutCts.Cancel();
                return await waiter.Completion.Task.ConfigureAwait(false);
            }

            // 超时/取消：若已被授予则视为成功，由调用方负责释放；否则标记放弃。
            lock (_sync)
            {
                if (waiter.Granted)
                {
                    return true;
                }

                waiter.Abandoned = true;
                queue.Remove(waiter);
            }

            return false;
        }

        private void GrantNextWriterLocked()
        {
            while (_writers.First is { } node)
            {
                _writers.RemoveFirst();
                var waiter = node.Value;
                if (waiter.Abandoned)
                {
                    continue;
                }

                _writerActive = true;
                waiter.Granted = true;
                waiter.Completion.TrySetResult(true);
                return;
            }

            // 没有有效写者时，允许读者进入。
            GrantReadersLocked();
        }

        private void GrantReadersLocked()
        {
            if (_writerActive || _writers.Count > 0)
            {
                return;
            }

            while (_readers.First is { } node)
            {
                _readers.RemoveFirst();
                var waiter = node.Value;
                if (waiter.Abandoned)
                {
                    continue;
                }

                _activeReaders++;
                waiter.Granted = true;
                waiter.Completion.TrySetResult(true);
            }
        }
    }

    private sealed class Waiter
    {
        public TaskCompletionSource<bool> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Granted { get; set; }

        public bool Abandoned { get; set; }
    }
}
