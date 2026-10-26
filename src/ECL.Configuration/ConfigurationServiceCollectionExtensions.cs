// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Configuration.Options;
using Ecl.Configuration.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace Ecl.Configuration;

public static class ConfigurationServiceCollectionExtensions
{
    public static IServiceCollection AddEclConfiguration(this IServiceCollection services)
    {
        services.AddSingleton<IOptionProvider, LauncherOptionsProvider>();
        services.AddSingleton<OptionRegistry>();
        services.AddSingleton<SettingsFileStore>();
        services.AddSingleton<ISettingsService, SettingsService>();
        return services;
    }
}
