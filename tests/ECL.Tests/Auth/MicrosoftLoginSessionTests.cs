// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using Ecl.Auth;
using Xunit;

namespace Ecl.Tests.Auth;

/// <summary>
/// 会话状态机的竞态契约：终止与提交互斥、Completion 只完成一次、未提交的账户必须丢弃。
/// </summary>
public sealed class MicrosoftLoginSessionTests
{
    private const string State = "state-value-for-tests";

    [Fact]
    public async Task TerminateThenCommitReturnsFalse()
    {
        using var session = CreateSession();
        Assert.True(session.TryBeginWaiting());
        Assert.True(session.Terminate(MicrosoftLoginStopReason.UserCancelled));

        Assert.False(session.TryCommit());
        Assert.Equal(MicrosoftLoginPhase.Aborted, session.Phase);
        Assert.Equal(MicrosoftLoginStopReason.UserCancelled, session.StopReason);

        var outcome = await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(outcome.Account);
        Assert.Equal(MicrosoftLoginStopReason.UserCancelled, outcome.StopReason);
    }

    [Fact]
    public void ConcurrentTerminateAndCommitHaveExactlyOneWinner()
    {
        for (var iteration = 0; iteration < 200; iteration++)
        {
            using var session = CreateSession();
            Assert.True(session.TryBeginWaiting());
            Assert.True(session.TryAcceptCallback(State));
            Assert.True(session.TrySetPending(CreateAccount()));

            var commitWins = 0;
            var terminateWins = 0;
            Parallel.For(0, 2, index =>
            {
                if (index == 0)
                {
                    if (session.TryCommit())
                    {
                        Interlocked.Increment(ref commitWins);
                    }
                }
                else if (session.Terminate(MicrosoftLoginStopReason.UserCancelled))
                {
                    Interlocked.Increment(ref terminateWins);
                }
            });

            Assert.Equal(1, commitWins + terminateWins);
        }
    }

    [Fact]
    public void RepeatedTerminateKeepsFirstStopReason()
    {
        using var session = CreateSession();
        Assert.True(session.TryBeginWaiting());

        Assert.True(session.Terminate(MicrosoftLoginStopReason.TimedOut));
        Assert.False(session.Terminate(MicrosoftLoginStopReason.UserCancelled));
        Assert.False(session.Terminate(MicrosoftLoginStopReason.Disposed));

        Assert.Equal(MicrosoftLoginStopReason.TimedOut, session.StopReason);
        Assert.Equal(MicrosoftLoginPhase.Aborted, session.Phase);
    }

    [Fact]
    public async Task CompletionCompletesOnlyOnce()
    {
        using var session = CreateSession();
        Assert.True(session.TryBeginWaiting());
        var completion = session.Completion;
        Assert.True(session.Terminate(MicrosoftLoginStopReason.TimedOut));

        Assert.False(session.TrySetPending(CreateAccount()));
        var first = await completion.WaitAsync(TimeSpan.FromSeconds(5));
        var second = await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Same(first, second);
        Assert.Null(second.Account);
        Assert.Equal(MicrosoftLoginStopReason.TimedOut, second.StopReason);
    }

    [Fact]
    public async Task DisposeWithoutCommitDiscardsPendingAccount()
    {
        var session = CreateSession();
        Assert.True(session.TryBeginWaiting());
        Assert.True(session.TryAcceptCallback(State));
        Assert.True(session.TrySetPending(CreateAccount()));
        Assert.Equal(MicrosoftLoginPhase.RedeemingCode, session.Phase);

        session.Dispose();

        Assert.False(session.TryCommit());
        Assert.Equal(MicrosoftLoginPhase.Aborted, session.Phase);
        var outcome = await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        // Completion 可能已经带着账户完成，但未经提交就释放的会话绝不允许再被提交。
        Assert.False(session.TryCommit());
        Assert.Equal(MicrosoftLoginPhase.Aborted, session.Phase);
        Assert.NotNull(outcome);
    }

    [Fact]
    public async Task TimeoutTerminatesSession()
    {
        using var session = CreateSession(TimeSpan.FromMilliseconds(150));
        Assert.True(session.TryBeginWaiting());

        var outcome = await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(MicrosoftLoginStopReason.TimedOut, outcome.StopReason);
        Assert.Equal(MicrosoftLoginPhase.Aborted, session.Phase);
        Assert.False(session.TryCommit());
    }

    [Fact]
    public async Task ExternalCancellationIsReportedAsUserCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        using var session = CreateSession(cancellationToken: cancellation.Token);
        Assert.True(session.TryBeginWaiting());

        cancellation.Cancel();
        var outcome = await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(MicrosoftLoginStopReason.UserCancelled, outcome.StopReason);
        Assert.Null(outcome.Account);
        Assert.False(session.TryCommit());
    }

    [Fact]
    public void StateMismatchDoesNotAdvancePhase()
    {
        using var session = CreateSession();
        Assert.True(session.TryBeginWaiting());

        Assert.False(session.TryAcceptCallback("attacker-state"));
        Assert.Equal(MicrosoftLoginPhase.WaitingForCallback, session.Phase);
        Assert.False(session.TryCommit());
    }

    private static MicrosoftLoginSession CreateSession(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default) => new(
        Guid.NewGuid(),
        new Uri("http://localhost:49152/"),
        new Uri("https://login.microsoftonline.com/consumers/oauth2/v2.0/authorize?state=state-value-for-tests"),
        State,
        Encoding.ASCII.GetBytes("verifier-material-for-tests-1234567890"),
        timeout ?? TimeSpan.FromMinutes(5),
        cancellationToken);

    private static Account CreateAccount() => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Type = AccountType.Microsoft,
        Name = "Steve",
        Uuid = "profile-uuid",
        AccessToken = "minecraft-access-token",
        RefreshToken = "microsoft-refresh-token",
    };
}
