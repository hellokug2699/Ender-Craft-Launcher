// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Windows;
using Ecl.Auth;
using Ecl.Core;
using Ecl.Core.Localization;

namespace Ecl.App.Views;

/// <summary>
/// Authlib-Injector 账户的唯一入口：新建账户与账户设置共用同一个对话框，
/// 认证服务器地址（必填）与注册链接（可选）只在这里出现，不写死任何服务器。
/// </summary>
public partial class AuthlibAccountDialog : Window
{
    private readonly IAuthServerService _authServers;
    private readonly IAccountService _accounts;
    private readonly ILocalizer _localizer;
    private readonly AuthServer? _server;
    private readonly Account? _account;
    private bool _busy;

    public AuthlibAccountDialog(
        IAuthServerService authServers,
        IAccountService accounts,
        ILocalizer localizer,
        AuthServer? server = null,
        Account? account = null)
    {
        _authServers = authServers;
        _accounts = accounts;
        _localizer = localizer;
        _server = server;
        _account = account;

        InitializeComponent();

        if (account is null)
        {
            ConfigureCreateMode();
        }
        else
        {
            ConfigureSettingsMode(account);
        }

        RegisterUrlBox.TextChanged += (_, _) => UpdateRegisterButton();
        UpdateRegisterButton();
        Loaded += (_, _) => ServerUrlBox.Focus();
    }

    /// <summary>对话框完成后保存的账号（新建或重新登录后的账号）。</summary>
    public Account? ResultAccount { get; private set; }

    /// <summary>对话框完成后保存的认证服务器。</summary>
    public AuthServer? ResultServer { get; private set; }

    private void ConfigureCreateMode()
    {
        Title = _localizer.Get("authlib.dialog.newTitle");
        IntroText.Text = _localizer.Get("authlib.dialog.newIntro");
        PasswordHintText.Text = _localizer.Get("authlib.passwordHintNew");
        ConfirmButton.Content = _localizer.Get("authlib.confirmSignIn");
    }

    private void ConfigureSettingsMode(Account account)
    {
        Title = _localizer.Get("authlib.dialog.settingsTitle");
        IntroText.Text = _localizer.Get("authlib.dialog.settingsIntro", account.Name, _server?.Name ?? string.Empty);
        PasswordHintText.Text = _localizer.Get("authlib.passwordHintSettings");
        ConfirmButton.Content = _localizer.Get("authlib.confirmSave");

        if (_server is not null)
        {
            ServerUrlBox.Text = _server.Url;
            RegisterUrlBox.Text = _server.RegisterUrl ?? string.Empty;
        }

        UsernameBox.Text = account.Name;
        UsernameBox.IsReadOnly = true;
    }

    private void UpdateRegisterButton()
    {
        var text = RegisterUrlBox.Text.Trim();
        OpenRegisterButton.IsEnabled =
            Uri.TryCreate(text, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    private void OnOpenRegisterClick(object sender, RoutedEventArgs e)
    {
        var text = RegisterUrlBox.Text.Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return;
        }

        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    private async void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        var serverUrl = ServerUrlBox.Text.Trim();
        if (serverUrl.Length == 0)
        {
            StatusText.Text = _localizer.Get("authlib.serverRequired");
            return;
        }

        if (_account is null)
        {
            await CreateAccountAsync(serverUrl).ConfigureAwait(true);
        }
        else
        {
            await SaveSettingsAsync(serverUrl).ConfigureAwait(true);
        }
    }

    private async Task CreateAccountAsync(string serverUrl)
    {
        var username = UsernameBox.Text.Trim();
        if (username.Length == 0)
        {
            StatusText.Text = _localizer.Get("authlib.usernameRequired");
            return;
        }

        var password = PasswordBox.Password;
        if (password.Length == 0)
        {
            StatusText.Text = _localizer.Get("authlib.passwordRequired");
            return;
        }

        SetBusy(true, _localizer.Get("authlib.working"));
        try
        {
            var server = await _authServers
                .AddAsync(serverUrl, RegisterUrlBox.Text.Trim())
                .ConfigureAwait(true);
            if (!server.IsSuccess)
            {
                ShowError("authlib.serverFailed", server.Error);
                return;
            }

            var signedIn = await _accounts
                .SignInThirdPartyAsync(server.Value!.Id, username, password)
                .ConfigureAwait(true);
            if (!signedIn.IsSuccess)
            {
                ShowError("authlib.loginFailed", signedIn.Error);
                return;
            }

            ResultServer = server.Value;
            ResultAccount = signedIn.Value;
            DialogResult = true;
        }
        catch (Exception exception)
        {
            StatusText.Text = _localizer.Get("authlib.loginFailed", "auth.exception", exception.Message);
        }
        finally
        {
            SetBusy(false, string.Empty);
        }
    }

    private async Task SaveSettingsAsync(string serverUrl)
    {
        if (_account is null || _server is null)
        {
            return;
        }

        if (!AuthlibAuthenticationService.TryNormalizeUrl(serverUrl, out var normalizedUrl))
        {
            StatusText.Text = _localizer.Get(
                "authlib.invalidServerUrl",
                AuthlibAuthenticationService.HttpsRequiredMessage);
            return;
        }

        var password = PasswordBox.Password;
        var registerUrl = RegisterUrlBox.Text.Trim();
        var urlChanged = !string.Equals(normalizedUrl, _server.Url, StringComparison.OrdinalIgnoreCase);

        SetBusy(true, _localizer.Get("authlib.working"));
        try
        {
            if (urlChanged)
            {
                if (password.Length == 0)
                {
                    // 地址变更会同时改变凭据所属的服务器；不允许在未重新登录时静默提交。
                    StatusText.Text = _localizer.Get("authlib.reauthRequired");
                    return;
                }

                // 1. 先验证新服务器。
                var metadata = await _authServers.ValidateAsync(normalizedUrl).ConfigureAwait(true);
                if (!metadata.IsSuccess)
                {
                    ShowError("authlib.serverFailed", metadata.Error);
                    return;
                }

                // 2. 用新地址完成登录，但先不写入任何本地状态。
                var targetServer = _server with
                {
                    Url = normalizedUrl,
                    Name = metadata.Value!.ServerName,
                    RegisterUrl = string.IsNullOrWhiteSpace(registerUrl)
                        ? metadata.Value.RegisterUrl
                        : registerUrl,
                };

                var prepared = await _accounts
                    .PrepareThirdPartyReauthenticationAsync(_account.Id, password, targetServer)
                    .ConfigureAwait(true);
                if (!prepared.IsSuccess)
                {
                    ShowError("authlib.loginFailed", prepared.Error);
                    return;
                }

                // 3. 提交服务器地址，再提交账户凭据与资料。
                var updated = await _authServers
                    .UpdateAsync(_server.Id, normalizedUrl, registerUrl)
                    .ConfigureAwait(true);
                if (!updated.IsSuccess)
                {
                    ShowError("authlib.serverFailed", updated.Error);
                    return;
                }

                var applied = await _accounts
                    .ApplyThirdPartyReauthenticationAsync(_account.Id, prepared.Value!, updated.Value!)
                    .ConfigureAwait(true);
                if (!applied.IsSuccess)
                {
                    // 凭据保存失败时尽量恢复旧地址，避免账号指向“新服务器 + 旧凭据”。
                    var rollback = await _authServers
                        .UpdateAsync(_server.Id, _server.Url, _server.RegisterUrl)
                        .ConfigureAwait(true);
                    if (rollback.IsSuccess)
                    {
                        ShowError("authlib.loginFailed", applied.Error);
                    }
                    else
                    {
                        StatusText.Text = _localizer.Get(
                            "authlib.reauthServerChanged",
                            applied.Error?.Message ?? string.Empty);
                    }

                    return;
                }

                ResultServer = updated.Value;
                ResultAccount = applied.Value;
                DialogResult = true;
                return;
            }

            var result = await _authServers
                .UpdateAsync(_server.Id, normalizedUrl, registerUrl)
                .ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                ShowError("authlib.serverFailed", result.Error);
                return;
            }

            ResultServer = result.Value;

            if (password.Length > 0)
            {
                // 地址未变，只重新登录并同步服务器上的最新角色信息。
                var signedIn = await _accounts
                    .ReauthenticateThirdPartyAsync(_account.Id, password)
                    .ConfigureAwait(true);
                if (!signedIn.IsSuccess)
                {
                    ShowError("authlib.loginFailed", signedIn.Error);
                    return;
                }

                ResultAccount = signedIn.Value;
            }
            else
            {
                ResultAccount = _account;
            }

            DialogResult = true;
        }
        catch (Exception exception)
        {
            StatusText.Text = _localizer.Get("authlib.loginFailed", "auth.exception", exception.Message);
        }
        finally
        {
            SetBusy(false, string.Empty);
        }
    }

    private void ShowError(string key, EclError? error)
    {
        StatusText.Text = _localizer.Get(key, error?.Code ?? "error", error?.Message ?? string.Empty);
    }

    private void SetBusy(bool busy, string status)
    {
        _busy = busy;
        ConfirmButton.IsEnabled = !busy;
        ServerUrlBox.IsEnabled = !busy;
        RegisterUrlBox.IsEnabled = !busy;
        UsernameBox.IsEnabled = !busy;
        PasswordBox.IsEnabled = !busy;
        if (status.Length > 0)
        {
            StatusText.Text = status;
        }
    }
}
