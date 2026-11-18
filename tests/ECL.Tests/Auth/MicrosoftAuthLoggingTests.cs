// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Auth;
using Xunit;

namespace Ecl.Tests.Auth;

/// <summary>
/// ADR 0065 的日志约束：只允许出现 sessionId、脱敏回调地址、phase、stop reason 与错误类型；
/// 授权码、state、PKCE verifier 与任何 token 都不得进入日志。
/// </summary>
public sealed class MicrosoftAuthLoggingTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task SuccessfulSignInLogsSessionAndPhaseWithoutSecrets()
    {
        var logger = new CapturingLogger<MicrosoftAuthenticationService>();
        using var harness = new MicrosoftSignInTestHarness(logger);

        var started = harness.Service.BeginSignIn(
            MicrosoftSignInTestHarness.Options(),
            harness.OpenBrowserAsync);
        Assert.True(started.IsSuccess, started.Error?.ToString());

        using var session = started.Value!;
        var authorizeUri = await harness.BrowserOpened.Task.WaitAsync(TestTimeout);
        var state = MicrosoftSignInTestHarness.ParseQuery(authorizeUri)["state"];

        harness.Server.Complete(MicrosoftSignInTestHarness.CreateCallback(state));
        var outcome = await session.Completion.WaitAsync(TestTimeout);
        Assert.True(session.TryCommit());
        Assert.NotNull(outcome.Account);

        var log = string.Join('\n', logger.Messages);
        Assert.Contains(session.Id.ToString(), log, StringComparison.Ordinal);
        Assert.Contains("stop=None", log, StringComparison.Ordinal);

        AssertNoSecrets(log, state, harness.Handler.CodeVerifier, authorizeUri);
    }

    [Fact]
    public async Task TerminatedSignInLogsStopReasonWithoutSecrets()
    {
        var logger = new CapturingLogger<MicrosoftAuthenticationService>();
        using var harness = new MicrosoftSignInTestHarness(logger);

        var started = harness.Service.BeginSignIn(
            MicrosoftSignInTestHarness.Options(),
            harness.OpenBrowserAsync);
        Assert.True(started.IsSuccess, started.Error?.ToString());

        using var session = started.Value!;
        var authorizeUri = await harness.BrowserOpened.Task.WaitAsync(TestTimeout);
        var state = MicrosoftSignInTestHarness.ParseQuery(authorizeUri)["state"];

        harness.Server.Complete(MicrosoftSignInTestHarness.CreateCallback("attacker-state"));
        var outcome = await session.Completion.WaitAsync(TestTimeout);
        Assert.Equal(MicrosoftLoginStopReason.StateMismatch, outcome.StopReason);

        var log = string.Join('\n', logger.Messages);
        Assert.Contains(session.Id.ToString(), log, StringComparison.Ordinal);
        Assert.Contains("stop=StateMismatch", log, StringComparison.Ordinal);

        AssertNoSecrets(log, state, harness.Handler.CodeVerifier, authorizeUri);
        Assert.DoesNotContain("attacker-state", log, StringComparison.Ordinal);
    }

    private static void AssertNoSecrets(string log, string state, string? verifier, Uri authorizeUri)
    {
        Assert.DoesNotContain(MicrosoftSignInTestHarness.AuthCode, log, StringComparison.Ordinal);
        Assert.DoesNotContain(state, log, StringComparison.Ordinal);
        Assert.DoesNotContain(authorizeUri.Query.TrimStart('?'), log, StringComparison.Ordinal);
        Assert.DoesNotContain("client_id=", log, StringComparison.Ordinal);
        Assert.DoesNotContain("code_verifier=", log, StringComparison.Ordinal);
        Assert.DoesNotContain("state=", log, StringComparison.Ordinal);
        Assert.DoesNotContain("microsoft-access-token", log, StringComparison.Ordinal);
        Assert.DoesNotContain("microsoft-refresh-token", log, StringComparison.Ordinal);
        Assert.DoesNotContain("minecraft-access-token", log, StringComparison.Ordinal);
        Assert.DoesNotContain("xbox-token", log, StringComparison.Ordinal);
        Assert.DoesNotContain("xsts-token", log, StringComparison.Ordinal);

        if (!string.IsNullOrEmpty(verifier))
        {
            Assert.DoesNotContain(verifier, log, StringComparison.Ordinal);
        }
    }
}
