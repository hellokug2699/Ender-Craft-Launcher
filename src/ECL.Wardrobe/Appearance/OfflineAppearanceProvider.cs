// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Auth;

namespace Ecl.Wardrobe.Appearance;

/// <summary>
/// 离线账户 Provider。离线账户没有可调用的 Mojang/Microsoft 账户外观 API，
/// 因此只声明衣柜/实例本地能力；所有账户外观操作都必须返回 Unsupported。
/// <para>
/// 本地资源能否在游戏中显示，取决于客户端、服务端或兼容模组，Provider 不会做虚假承诺。
/// </para>
/// </summary>
public sealed class OfflineAppearanceProvider : IAppearanceProvider
{
    public const string Id = "offline";

    public const string AccountApiUnavailableMessage =
        "离线账户没有账户级皮肤/披风服务；本地资源只能在衣柜中管理，并可由实例本地配置使用。"
        + "游戏内是否显示取决于客户端、服务端或兼容模组，Provider 无法保证。";

    public string ProviderId => Id;

    public Task<AppearanceCapabilityReport> GetCapabilitiesAsync(
        Account account,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        cancellationToken.ThrowIfCancellationRequested();

        if (account.Type != AccountType.Offline)
        {
            return Task.FromResult(new AppearanceCapabilityReport
            {
                Capabilities = AppearanceCapability.None,
                Reason = "OfflineAppearanceProvider 只处理离线账户。",
            });
        }

        return Task.FromResult(new AppearanceCapabilityReport
        {
            Capabilities = AppearanceCapability.UseLocalWardrobe | AppearanceCapability.ApplyLocalToInstance,
            Reason = "离线账户只承诺本地衣柜与实例本地配置能力，不承诺账户外观同步。",
        });
    }

    public Task<AppearanceOperationResult> ExecuteAsync(
        AppearanceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.Account.Type != AccountType.Offline)
        {
            return Task.FromResult(AppearanceOperationResult.InvalidInput(
                "OfflineAppearanceProvider 只处理离线账户。"));
        }

        return Task.FromResult(AppearanceOperationResult.Unsupported(AccountApiUnavailableMessage));
    }
}
