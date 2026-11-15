// SPDX-License-Identifier: GPL-3.0-or-later

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.App.Dialogs;
using Ecl.App.Services;
using Ecl.Auth;
using Ecl.Core;
using Ecl.Core.Localization;

namespace Ecl.App.Dialogs.ViewModels;

/// <summary>
/// Authlib-Injector 账户弹窗内容（ADR 0052、ADR 0065、迁移规划 §6.2 #16）。
///
/// 新建账户与账户设置共用同一个内容对象；认证服务器地址（必填）与注册链接（可选）只在这里出现。
/// 密码只作为方法参数在本次调用中使用，不进入绑定属性、不写入日志、不进入普通持久状态；
/// 凭据持久化仍由 <see cref="IAccountService"/> 与安全凭据存储负责。
/// </summary>
public sealed partial class AuthlibAccountDialogViewModel : ObservableObject, IDialogSessionAware
{
    private readonly IAuthServerService _authServers;
    private readonly IAccountService _accounts;
    private readonly IExternalLinkService _links;
    private readonly ILocalizer _localizer;
    private readonly AuthServer? _server;
    private readonly Account? _account;
    private IDialogSession? _session;
    private Account? _resultAccount;
    private AuthServer? _resultServer;

    public AuthlibAccountDialogViewModel(
        IAuthServerService authServers,
        IAccountService accounts,
        IExternalLinkService links,
        ILocalizer localizer,
        AuthServer? server = null,
        Account? account = null)
    {
        ArgumentNullException.ThrowIfNull(authServers);
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(links);
        ArgumentNullException.ThrowIfNull(localizer);

        _authServers = authServers;
        _accounts = accounts;
        _links = links;
        _localizer = localizer;
        _server = server;
        _account = account;

        if (account is null)
        {
            IsSettingsMode = false;
            IntroText = localizer.Get("authlib.dialog.newIntro");
            PasswordHintText = localizer.Get("authlib.passwordHintNew");
            ConfirmText = localizer.Get("authlib.confirmSignIn");
        }
        else
        {
            IsSettingsMode = true;
            IntroText = localizer.Get("authlib.dialog.settingsIntro", account.Name, server?.Name ?? string.Empty);
            PasswordHintText = localizer.Get("authlib.passwordHintSettings");
            ConfirmText = localizer.Get("authlib.confirmSave");
            if (server is not null)
            {
                ServerUrl = server.Url;
                RegisterUrl = server.RegisterUrl ?? string.Empty;
            }

            Username = account.Name;
        }
    }

    public bool IsSettingsMode { get; }

    public string IntroText { get; }

    public string PasswordHintText { get; }

    public string ConfirmText { get; }

    public string CancelText => _localizer.Get("authlib.cancel");

    public bool IsUsernameReadOnly => _account is not null;

    [ObservableProperty]
    private string _serverUrl = string.Empty;

    [ObservableProperty]
    private string _registerUrl = string.Empty;

    [ObservableProperty]
    private string _username = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    public bool CanOpenRegister =>
        Uri.TryCreate(RegisterUrl.Trim(), UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    void IDialogSessionAware.Attach(IDialogSession session) => _session = session;

    [RelayCommand]
    private void OpenRegister()
    {
        var text = RegisterUrl.Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return;
        }

        if (!_links.Open(uri.AbsoluteUri))
        {
            StatusText = _localizer.Get("authlib.openRegisterFailed");
        }
    }

    /// <summary>
    /// 由视图在确认按钮点击时传入密码。密码只存在于本方法栈与领域调用中，不写入任何属性。
    /// </summary>
    public async Task SubmitAsync(string password)
    {
        ArgumentNullException.ThrowIfNull(password);

        if (IsBusy)
        {
            return;
        }

        var serverUrl = ServerUrl.Trim();
        if (serverUrl.Length == 0)
        {
            StatusText = _localizer.Get("authlib.serverRequired");
            return;
        }

        if (_account is null)
        {
            await CreateAccountAsync(serverUrl, password).ConfigureAwait(true);
        }
        else
        {
            await SaveSettingsAsync(serverUrl, password).ConfigureAwait(true);
        }
    }

    partial void OnRegisterUrlChanged(string value) => OnPropertyChanged(nameof(CanOpenRegister));

    private async Task CreateAccountAsync(string serverUrl, string password)
    {
        var username = Username.Trim();
        if (username.Length == 0)
        {
            StatusText = _localizer.Get("authlib.usernameRequired");
            return;
        }

        if (password.Length == 0)
        {
            StatusText = _localizer.Get("authlib.passwordRequired");
            return;
        }

        var success = false;
        SetBusy(true, _localizer.Get("authlib.working"));
        try
        {
            var server = await _authServers
                .AddAsync(serverUrl, RegisterUrl.Trim())
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

            _resultServer = server.Value;
            _resultAccount = signedIn.Value;
            success = true;
        }
        catch (Exception exception)
        {
            StatusText = _localizer.Get("authlib.loginFailed", "auth.exception", exception.Message);
        }
        finally
        {
            SetBusy(false, string.Empty);
        }

        if (success)
        {
            CloseWithResult();
        }
    }

    private async Task SaveSettingsAsync(string serverUrl, string password)
    {
        if (_account is null || _server is null)
        {
            return;
        }

        if (!AuthlibAuthenticationService.TryNormalizeUrl(serverUrl, out var normalizedUrl))
        {
            StatusText = _localizer.Get(
                "authlib.invalidServerUrl",
                AuthlibAuthenticationService.HttpsRequiredMessage);
            return;
        }

        var registerUrl = RegisterUrl.Trim();
        var urlChanged = !string.Equals(normalizedUrl, _server.Url, StringComparison.OrdinalIgnoreCase);

        var success = false;
        SetBusy(true, _localizer.Get("authlib.working"));
        try
        {
            if (urlChanged)
            {
                if (password.Length == 0)
                {
                    // 地址变更会同时改变凭据所属的服务器；不允许在未重新登录时静默提交。
                    StatusText = _localizer.Get("authlib.reauthRequired");
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
                        StatusText = _localizer.Get(
                            "authlib.reauthServerChanged",
                            applied.Error?.Message ?? string.Empty);
                    }

                    return;
                }

                _resultServer = updated.Value;
                _resultAccount = applied.Value;
                success = true;
            }
            else
            {
                var result = await _authServers
                    .UpdateAsync(_server.Id, normalizedUrl, registerUrl)
                    .ConfigureAwait(true);
                if (!result.IsSuccess)
                {
                    ShowError("authlib.serverFailed", result.Error);
                    return;
                }

                _resultServer = result.Value;

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

                    _resultAccount = signedIn.Value;
                }
                else
                {
                    _resultAccount = _account;
                }

                success = true;
            }
        }
        catch (Exception exception)
        {
            StatusText = _localizer.Get("authlib.loginFailed", "auth.exception", exception.Message);
        }
        finally
        {
            SetBusy(false, string.Empty);
        }

        if (success)
        {
            CloseWithResult();
        }
    }

    private void CloseWithResult()
    {
        if (_resultAccount is null)
        {
            return;
        }

        _session?.TryClose(
            DialogOutcome.Primary,
            new AuthlibAccountResult(_resultAccount, _resultServer));
    }

    private void ShowError(string key, EclError? error) =>
        StatusText = _localizer.Get(key, error?.Code ?? "error", error?.Message ?? string.Empty);

    private void SetBusy(bool busy, string status)
    {
        IsBusy = busy;
        _session?.SetBusy(busy);
        if (status.Length > 0)
        {
            StatusText = status;
        }
    }
}

/// <summary>Authlib 账户弹窗的最终结果：保存的账户与（可选的）认证服务器。</summary>
public sealed record AuthlibAccountResult(Account Account, AuthServer? Server);
