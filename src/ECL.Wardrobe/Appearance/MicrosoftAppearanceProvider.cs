// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Auth;

namespace Ecl.Wardrobe.Appearance;

/// <summary>
/// Microsoft 账户 Provider。
/// <para>
/// ADR 0037 要求：必须复用现有 Microsoft 认证链与账户服务，不得重复 OAuth 登录，
/// 不得自行持久化另一份 refresh token。当前 Minecraft Services 的皮肤/披风接口
/// 尚未逐项验证，因此本 Provider 声明零能力，所有操作都返回 Unsupported 并给出原因。
/// </para>
/// <para>
/// 这里保留 <see cref="AccountService"/> 引用，是为了明确未来实现时唯一允许的凭据来源；
/// 当前代码不会调用它，也不会发起任何网络请求，因此不会触发 OAuth 或写入第二份令牌。
/// </para>
/// </summary>
public sealed class MicrosoftAppearanceProvider : IAppearanceProvider
{
    public const string Id = "microsoft";

    public const string UnverifiedCapabilitiesMessage =
        "Minecraft Services 的皮肤/披风接口尚未逐项验证（请求格式、账户权限与服务端行为），"
        + "因此不声明任何外观能力，也不会伪报成功。";

    private readonly IAccountService? _accountService;

    public MicrosoftAppearanceProvider(IAccountService? accountService = null)
    {
        _accountService = accountService;
    }

    public string ProviderId => Id;

    /// <summary>未来实现时必须使用的凭据来源；当前不会读取或刷新令牌。</summary>
    public IAccountService? AccountService => _accountService;

    public Task<AppearanceCapabilityReport> GetCapabilitiesAsync(
        Account account,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        cancellationToken.ThrowIfCancellationRequested();

        if (account.Type != AccountType.Microsoft)
        {
            return Task.FromResult(new AppearanceCapabilityReport
            {
                Capabilities = AppearanceCapability.None,
                Reason = "MicrosoftAppearanceProvider 只处理 Microsoft 账户。",
            });
        }

        return Task.FromResult(new AppearanceCapabilityReport
        {
            Capabilities = AppearanceCapability.None,
            Reason = UnverifiedCapabilitiesMessage,
        });
    }

    public Task<AppearanceOperationResult> ExecuteAsync(
        AppearanceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.Account.Type != AccountType.Microsoft)
        {
            return Task.FromResult(AppearanceOperationResult.InvalidInput(
                "MicrosoftAppearanceProvider 只处理 Microsoft 账户。"));
        }

        // 关键安全约束：在接口逐项验证并完成真实账户测试前，不调用账户服务、不刷新令牌、
        // 不读取凭据，也不发送任何网络请求。
        return Task.FromResult(AppearanceOperationResult.Unsupported(
            UnverifiedCapabilitiesMessage,
            "Provider 未发起 OAuth 登录，也未保存第二份令牌。"));
    }
}
