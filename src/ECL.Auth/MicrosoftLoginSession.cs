// SPDX-License-Identifier: GPL-3.0-or-later

using System.Security.Cryptography;
using Ecl.Core;

namespace Ecl.Auth;

/// <summary>
/// 一次 Microsoft 登录会话所处的阶段。状态只允许前进：
/// Created → WaitingForCallback → RedeemingCode → Completed，异常路径随时进入 Aborted。
/// </summary>
public enum MicrosoftLoginPhase
{
    Created,
    WaitingForCallback,
    RedeemingCode,
    Completed,
    Aborted,
}

/// <summary>
/// 会话被终止的原因。调用方据此给出不同文案；<see cref="None"/> 表示正常结束。
/// </summary>
public enum MicrosoftLoginStopReason
{
    None,
    UserCancelled,
    TimedOut,
    CallbackRejected,
    StateMismatch,
    ProtocolViolation,
    TokenExchangeFailed,
    ListenerUnavailable,
    Disposed,
}

/// <summary>
/// 一次登录的最终结果。<see cref="Account"/> 非空只代表凭据已取得，**不代表账户已写入**；
/// 只有 <see cref="IMicrosoftLoginSession.TryCommit"/> 成功后才能把账户持久化。
/// </summary>
public sealed record MicrosoftLoginOutcome(
    Account? Account,
    MicrosoftLoginStopReason StopReason,
    EclError? Error);

/// <summary>
/// 可被显式终止的 Microsoft 登录会话。把“等待回调 → 兑换令牌 → 组装账户 → 提交账户”
/// 拆成可观察的阶段，使取消、超时与迟到回调都只能落在明确的停止原因上。
/// </summary>
public interface IMicrosoftLoginSession : IDisposable
{
    Guid Id { get; }

    Uri RedirectUri { get; }

    MicrosoftLoginPhase Phase { get; }

    MicrosoftLoginStopReason StopReason { get; }

    /// <summary>只完成一次；正常取得账户或任一终止路径都会完成它。</summary>
    Task<MicrosoftLoginOutcome> Completion { get; }

    event EventHandler? StateChanged;

    /// <summary>终止会话；只有真正完成状态迁移的那一次调用返回 <c>true</c>。</summary>
    bool Terminate(MicrosoftLoginStopReason reason);

    /// <summary>
    /// 提交结果：仅当处于 <see cref="MicrosoftLoginPhase.RedeemingCode"/> 时成功。
    /// 提交与终止互斥，因此“令牌已取得但用户已取消”的窗口里账户不会被写入。
    /// </summary>
    bool TryCommit();
}

/// <summary>
/// 会话状态机的唯一实现。所有阶段迁移都通过 <see cref="Interlocked.CompareExchange(ref int, int, int)"/>
/// 完成，保证终止、提交、超时与外部取消同时发生时只有一个赢家。
/// </summary>
internal sealed class MicrosoftLoginSession : IMicrosoftLoginSession
{
    private const int PhaseCreated = 0;
    private const int PhaseWaiting = 1;
    private const int PhaseRedeeming = 2;
    private const int PhaseCompleted = 3;
    private const int PhaseAborted = 4;

    private readonly object _gate = new();
    private readonly byte[] _verifier;
    private readonly string _state;
    private readonly CancellationToken _externalToken;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly CancellationToken _cancellationToken;
    private readonly TaskCompletionSource<MicrosoftLoginOutcome> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TimeSpan _timeout;
    private readonly TimeProvider _timeProvider;
    private CancellationTokenRegistration _externalRegistration;
    private ITimer? _timeoutTimer;
    private Account? _pendingAccount;
    private int _phase = PhaseCreated;
    private int _stopReason = (int)MicrosoftLoginStopReason.None;
    private int _disposed;

    internal MicrosoftLoginSession(
        Guid id,
        Uri redirectUri,
        Uri authorizeUri,
        string state,
        byte[] verifier,
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(redirectUri);
        ArgumentNullException.ThrowIfNull(authorizeUri);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(verifier);

        Id = id;
        RedirectUri = redirectUri;
        AuthorizeUri = authorizeUri;
        _state = state;
        // 调用方移交 verifier 的所有权：Dispose 时在内存里清零，避免长期驻留进程堆。
        _verifier = verifier;
        _timeout = timeout;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _externalToken = cancellationToken;
        _cancellationToken = _cancellation.Token;
    }

    public Guid Id { get; }

    public Uri RedirectUri { get; }

    /// <summary>浏览器授权地址；含 <c>state</c> 与 PKCE challenge，日志里绝不能原样输出。</summary>
    internal Uri AuthorizeUri { get; }

    public MicrosoftLoginPhase Phase => Volatile.Read(ref _phase) switch
    {
        PhaseCreated => MicrosoftLoginPhase.Created,
        PhaseWaiting => MicrosoftLoginPhase.WaitingForCallback,
        PhaseRedeeming => MicrosoftLoginPhase.RedeemingCode,
        PhaseCompleted => MicrosoftLoginPhase.Completed,
        _ => MicrosoftLoginPhase.Aborted,
    };

    public MicrosoftLoginStopReason StopReason => (MicrosoftLoginStopReason)Volatile.Read(ref _stopReason);

    public Task<MicrosoftLoginOutcome> Completion => _completion.Task;

    internal CancellationToken CancellationToken => _cancellationToken;

    public event EventHandler? StateChanged;

    /// <summary>
    /// 启动等待：先注册外部取消与超时，再迁移到 WaitingForCallback。
    /// 这样即使外部令牌在调用前就已取消，CAS 也必然失败，不会留下“看似等待中其实已终止”的会话。
    /// </summary>
    internal bool TryBeginWaiting()
    {
        var timerStarted = false;
        try
        {
            lock (_gate)
            {
                if (_timeout > TimeSpan.Zero)
                {
                    _timeoutTimer = _timeProvider.CreateTimer(
                        static state => ((MicrosoftLoginSession)state!).Terminate(MicrosoftLoginStopReason.TimedOut),
                        this,
                        _timeout,
                        Timeout.InfiniteTimeSpan);
                    timerStarted = true;
                }

                if (_externalToken.CanBeCanceled)
                {
                    _externalRegistration = _externalToken.Register(
                        static state => ((MicrosoftLoginSession)state!).Terminate(MicrosoftLoginStopReason.UserCancelled),
                        this);
                }
            }
        }
        catch
        {
            if (timerStarted)
            {
                StopWaitResources();
            }

            throw;
        }

        if (Interlocked.CompareExchange(ref _phase, PhaseWaiting, PhaseCreated) != PhaseCreated)
        {
            StopWaitResources();
            return false;
        }

        RaiseStateChanged();
        return true;
    }

    /// <summary>
    /// 校验回调 state。只在仍处于 WaitingForCallback 且 state 完全一致时迁移到 RedeemingCode；
    /// 返回 <c>false</c> 时调用方必须先确认会话是否已被终止，再决定是否标记 StateMismatch。
    /// </summary>
    internal bool TryAcceptCallback(string? returnedState)
    {
        if (Volatile.Read(ref _phase) != PhaseWaiting)
        {
            return false;
        }

        if (!string.Equals(_state, returnedState, StringComparison.Ordinal))
        {
            return false;
        }

        return Interlocked.CompareExchange(ref _phase, PhaseRedeeming, PhaseWaiting) == PhaseWaiting;
    }

    /// <summary>
    /// 暂存刚取得的账户并完成 <see cref="Completion"/>。此时账户**尚未提交**：
    /// 若随后发生取消/超时，<see cref="TryCommit"/> 会失败，调用方必须丢弃该账户。
    /// </summary>
    internal bool TrySetPending(Account account)
    {
        ArgumentNullException.ThrowIfNull(account);

        lock (_gate)
        {
            if (Volatile.Read(ref _phase) != PhaseRedeeming)
            {
                return false;
            }

            _pendingAccount = account;
        }

        if (!_completion.TrySetResult(new MicrosoftLoginOutcome(account, MicrosoftLoginStopReason.None, null)))
        {
            return false;
        }

        RaiseStateChanged();
        return true;
    }

    /// <summary>供兑换请求读取 verifier 的短期副本；调用方用完必须清零。</summary>
    internal byte[] CopyVerifier()
    {
        lock (_gate)
        {
            return (byte[])_verifier.Clone();
        }
    }

    public bool TryCommit()
    {
        if (Interlocked.CompareExchange(ref _phase, PhaseCompleted, PhaseRedeeming) != PhaseRedeeming)
        {
            return false;
        }

        Account? account;
        lock (_gate)
        {
            account = _pendingAccount;
        }

        if (account is null)
        {
            // 没有待提交账户却进入提交：回退为终止，避免出现“已完成但没有账户”的假成功。
            Interlocked.Exchange(ref _phase, PhaseAborted);
            Volatile.Write(ref _stopReason, (int)MicrosoftLoginStopReason.ProtocolViolation);
            StopWaitResources();
            _completion.TrySetResult(new MicrosoftLoginOutcome(
                null,
                MicrosoftLoginStopReason.ProtocolViolation,
                new EclError("auth.login_terminated", "登录会话没有可提交的账户。")));
            RaiseStateChanged();
            return false;
        }

        StopWaitResources();
        RaiseStateChanged();
        return true;
    }

    public bool Terminate(MicrosoftLoginStopReason reason) => Terminate(reason, null);

    internal bool Terminate(MicrosoftLoginStopReason reason, EclError? error)
    {
        while (true)
        {
            var phase = Volatile.Read(ref _phase);
            if (phase is PhaseCompleted or PhaseAborted)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _phase, PhaseAborted, phase) != phase)
            {
                continue;
            }

            // 先固定停止原因，再让等待中的回调与网络请求看到取消，最后完成结果；
            // 观察者因此不会看到“已终止但结果仍是成功”的中间态。
            Volatile.Write(ref _stopReason, (int)reason);
            StopWaitResources();
            try
            {
                _cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Dispose 已抢先释放令牌源；状态与结果仍然有效。
            }

            _completion.TrySetResult(new MicrosoftLoginOutcome(null, reason, error));
            RaiseStateChanged();
            return true;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // 未提交就释放：账户必须丢弃，防止调用方在取消后仍拿到可用凭据。
        Terminate(MicrosoftLoginStopReason.Disposed);
        StopWaitResources();
        CryptographicOperations.ZeroMemory(_verifier);
        _cancellation.Dispose();
    }

    private void StopWaitResources()
    {
        ITimer? timer;
        lock (_gate)
        {
            timer = _timeoutTimer;
            _timeoutTimer = null;
            _externalRegistration.Unregister();
            _externalRegistration = default;
        }

        timer?.Dispose();
    }

    private void RaiseStateChanged()
    {
        var handlers = StateChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception)
            {
                // 观察者异常不能破坏登录状态机，否则一次 UI 回调异常就会让会话卡在中间态。
            }
        }
    }
}
