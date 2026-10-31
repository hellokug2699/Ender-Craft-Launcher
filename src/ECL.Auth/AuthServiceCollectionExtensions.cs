// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.Extensions.DependencyInjection;

namespace Ecl.Auth;

public static class AuthServiceCollectionExtensions
{
    public static IServiceCollection AddEclAuth(this IServiceCollection services)
    {
        services.AddSingleton<IAccountStore, JsonAccountStore>();
        services.AddSingleton<ISecureCredentialStore, WindowsDpapiCredentialStore>();
        services.AddSingleton<IAccountService, AccountService>();
        services.AddSingleton<IMicrosoftAuthenticationService, MicrosoftAuthenticationService>();
        return services;
    }
}
