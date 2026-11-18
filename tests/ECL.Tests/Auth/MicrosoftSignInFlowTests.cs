// SPDX-License-Identifier: GPL-3.0-or-later

using System.Security.Cryptography;
using System.Text;
using Ecl.Auth;
using Ecl.Core;
using Xunit;

namespace Ecl.Tests.Auth;

/// <summary>
/// F-11 登录流程的安全契约：state 先校验后兑换、终止后迟到回调不生效、
/// 超时与监听失败有稳定错误码、非法授权地址不会打开浏览器。
/// </summary>
public sealed class MicrosoftSignInFlowTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task SuccessfulSignInValidatesStateAndSubmitsAccount()
    {
        using var harness = new MicrosoftSignInTestHarness();
        var started = harness.Service.BeginSignIn(
            MicrosoftSignInTestHarness.Options(),
            harness.OpenBrowserAsync);
        Assert.True(started.IsSuccess, started.Error?.ToString());

        using var session = started.Value!;
        var authorizeUri = await harness.BrowserOpened.Task.WaitAsync(TestTimeout);
        var query = MicrosoftSignInTestHarness.ParseQuery(authorizeUri);
        var state = query["state"];

        harness.Server.Complete(MicrosoftSignInTestHarness.CreateCallback(state));
        var outcome = await session.Completion.WaitAsync(TestTimeout);

        Assert.Equal(MicrosoftLoginStopReason.None, outcome.StopReason);
        Assert.NotNull(outcome.Account);
        Assert.Equal("Steve", outcome.Account!.Name);
        Assert.Equal("profile-uuid", outcome.Account.Uuid);
        Assert.Equal("minecraft-access-token", outcome.Account.AccessToken);
        Assert.Equal("microsoft-refresh-token", outcome.Account.RefreshToken);

        Assert.True(session.TryCommit());
        Assert.Equal(MicrosoftLoginPhase.Completed, session.Phase);

        // 监听必须在 state 校验前就关闭，端口不再接收第二个回调。
        Assert.True(harness.Server.Disposed);
        Assert.Equal(1, harness.Handler.TokenRequestCount);
        Assert.Equal("authorization_code", harness.Handler.GrantType);
        Assert.Equal(MicrosoftSignInTestHarness.AuthCode, harness.Handler.AuthorizationCode);
        Assert.Equal(session.RedirectUri.AbsoluteUri, harness.Handler.RedirectUri);
        Assert.Equal(session.RedirectUri.AbsoluteUri, query["redirect_uri"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal("code", query["response_type"]);

        // PKCE：授权请求里的 challenge 必须等于兑换请求中 verifier 的 S256 摘要。
        Assert.False(string.IsNullOrWhiteSpace(harness.Handler.CodeVerifier));
        var expectedChallenge = Base64Url(
            SHA256.HashData(Encoding.ASCII.GetBytes(harness.Handler.CodeVerifier!)));
        Assert.Equal(expectedChallenge, query["code_challenge"]);
    }

    [Fact]
    public async Task StateMismatchNeverExchangesCode()
    {
        using var harness = new MicrosoftSignInTestHarness();
        var started = harness.Service.BeginSignIn(
            MicrosoftSignInTestHarness.Options(),
            harness.OpenBrowserAsync);
        Assert.True(started.IsSuccess, started.Error?.ToString());

        using var session = started.Value!;
        await harness.BrowserOpened.Task.WaitAsync(TestTimeout);

        harness.Server.Complete(MicrosoftSignInTestHarness.CreateCallback("attacker-state"));
        var outcome = await session.Completion.WaitAsync(TestTimeout);

        Assert.Equal(MicrosoftLoginStopReason.StateMismatch, outcome.StopReason);
        Assert.Null(outcome.Account);
        Assert.Equal(0, harness.Handler.TokenRequestCount);
        Assert.False(session.TryCommit());
        Assert.True(harness.Server.Disposed);
    }

    [Fact]
    public async Task AccessDeniedNeverExchangesCode()
    {
        using var harness = new MicrosoftSignInTestHarness();
        var started = harness.Service.BeginSignIn(
            MicrosoftSignInTestHarness.Options(),
            harness.OpenBrowserAsync);
        Assert.True(started.IsSuccess, started.Error?.ToString());

        using var session = started.Value!;
        var authorizeUri = await harness.BrowserOpened.Task.WaitAsync(TestTimeout);
        var state = MicrosoftSignInTestHarness.ParseQuery(authorizeUri)["state"];

        harness.Server.Complete(MicrosoftSignInTestHarness.CreateCallback(state, error: "access_denied"));
        var outcome = await session.Completion.WaitAsync(TestTimeout);

        Assert.Equal(MicrosoftLoginStopReason.CallbackRejected, outcome.StopReason);
        Assert.Null(outcome.Account);
        Assert.Equal(0, harness.Handler.TokenRequestCount);
        Assert.False(session.TryCommit());
    }

    [Fact]
    public async Task LateCallbackAfterTerminationIsDiscarded()
    {
        using var harness = new MicrosoftSignInTestHarness();
        var started = harness.Service.BeginSignIn(
            MicrosoftSignInTestHarness.Options(),
            harness.OpenBrowserAsync);
        Assert.True(started.IsSuccess, started.Error?.ToString());

        using var session = started.Value!;
        var authorizeUri = await harness.BrowserOpened.Task.WaitAsync(TestTimeout);
        var state = MicrosoftSignInTestHarness.ParseQuery(authorizeUri)["state"];

        Assert.True(session.Terminate(MicrosoftLoginStopReason.UserCancelled));

        // 终止之后浏览器才带着合法回调到达：授权码不得被兑换，账户也不得出现。
        harness.Server.Complete(MicrosoftSignInTestHarness.CreateCallback(state));
        var outcome = await session.Completion.WaitAsync(TestTimeout);
        await Task.Delay(100);

        Assert.Equal(MicrosoftLoginStopReason.UserCancelled, outcome.StopReason);
        Assert.Null(outcome.Account);
        Assert.Equal(0, harness.Handler.TokenRequestCount);
        Assert.False(session.TryCommit());
    }

    [Fact]
    public async Task TimeoutMapsToLoginTimeoutError()
    {
        using var harness = new MicrosoftSignInTestHarness();
        var result = await harness.Service.SignInAsync(
            MicrosoftSignInTestHarness.Options(TimeSpan.FromMilliseconds(200)),
            harness.OpenBrowserAsync);

        Assert.False(result.IsSuccess);
        Assert.Equal("auth.login_timeout", result.Error!.Code);
        Assert.Equal(0, harness.Handler.TokenRequestCount);
        Assert.True(harness.Server.Disposed);
    }

    [Fact]
    public void LoopbackListenerFailureDoesNotOpenBrowser()
    {
        var browserOpened = false;
        using var harness = new MicrosoftSignInTestHarness(
            startLoopbackServer: (_, _, _) => Result<IMicrosoftLoopbackServer>.Fail(
                "auth.callback_listener_unavailable",
                "simulated listener failure"));

        var started = harness.Service.BeginSignIn(
            MicrosoftSignInTestHarness.Options(),
            _ =>
            {
                browserOpened = true;
                return Task.CompletedTask;
            });

        Assert.False(started.IsSuccess);
        Assert.Equal("auth.callback_listener_unavailable", started.Error!.Code);
        Assert.False(browserOpened);
    }

    [Theory]
    [InlineData("https://evil.example.com/consumers", "auth.invalid_authority")]
    [InlineData("http://login.microsoftonline.com/consumers", "auth.invalid_authority")]
    [InlineData("https://login.microsoftonline.com.evil.example/consumers", "auth.invalid_authority")]
    public void InvalidAuthorityIsRejectedBeforeBrowser(string authority, string expectedCode)
    {
        var browserOpened = false;
        var listenerStarted = false;
        using var harness = new MicrosoftSignInTestHarness(
            startLoopbackServer: (_, _, _) =>
            {
                listenerStarted = true;
                return Result<IMicrosoftLoopbackServer>.Ok(new FakeLoopbackServer());
            });

        var started = harness.Service.BeginSignIn(
            MicrosoftSignInTestHarness.Options() with { Authority = authority },
            _ =>
            {
                browserOpened = true;
                return Task.CompletedTask;
            });

        Assert.False(started.IsSuccess);
        Assert.Equal(expectedCode, started.Error!.Code);
        Assert.False(browserOpened);
        Assert.False(listenerStarted);
    }

    [Fact]
    public void NonLoopbackRedirectHostIsRejectedBeforeBrowser()
    {
        var browserOpened = false;
        using var harness = new MicrosoftSignInTestHarness();

        var started = harness.Service.BeginSignIn(
            MicrosoftSignInTestHarness.Options() with { RedirectHost = "192.168.1.20" },
            _ =>
            {
                browserOpened = true;
                return Task.CompletedTask;
            });

        Assert.False(started.IsSuccess);
        Assert.Equal("auth.invalid_redirect_host", started.Error!.Code);
        Assert.False(browserOpened);
        Assert.False(harness.Server.Disposed);
    }

    [Fact]
    public void InvalidClientIdIsRejected()
    {
        using var harness = new MicrosoftSignInTestHarness();
        var started = harness.Service.BeginSignIn(
            MicrosoftSignInTestHarness.Options() with { ClientId = "not-a-guid" },
            harness.OpenBrowserAsync);

        Assert.False(started.IsSuccess);
        Assert.Equal("auth.invalid_client_id", started.Error!.Code);
        Assert.Equal(0, harness.BrowserOpenCount);
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
