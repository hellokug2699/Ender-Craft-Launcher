// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.Extensions.DependencyInjection;

namespace Ecl.Auth;

public static class AuthServiceCollectionExtensions
{
    public static IServiceCollection AddEclAuth(this IServiceCollection services)
    {
        services.AddSingleton<IAccountStore, JsonAccountStore>();
        services.AddSingleton<IAuthServerStore, JsonAuthServerStore>();
        services.AddSingleton<ISecureCredentialStore, WindowsDpapiCredentialStore>();
        services.AddSingleton<IAccountService, AccountService>();
        services.AddSingleton<IAuthServerService, AuthServerService>();
        // 同一个服务同时提供旧的 SignInAsync 兼容入口和 F-11 的可终止会话入口，
        // 保证两条路径使用完全相同的校验与脱敏逻辑。
        services.AddSingleton<MicrosoftAuthenticationService>();
        services.AddSingleton<IMicrosoftAuthenticationService>(
            provider => provider.GetRequiredService<MicrosoftAuthenticationService>());
        services.AddSingleton<IMicrosoftLoginSessionService>(
            provider => provider.GetRequiredService<MicrosoftAuthenticationService>());
        services.AddSingleton<IAuthlibAuthenticationService, AuthlibAuthenticationService>();
        return services;
    }
}
