// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ecl.Sessions;

public static class SessionServiceCollectionExtensions
{
    public static IServiceCollection AddEclSessions(this IServiceCollection services)
    {
        services.TryAddSingleton<IGameSessionService, GameSessionService>();
        services.TryAddSingleton<ISessionExportService, SessionExportService>();
        services.TryAddSingleton<SessionArtifactCollector>();
        services.TryAddSingleton<ISessionRetentionService, SessionRetentionService>();
        return services;
    }
}
