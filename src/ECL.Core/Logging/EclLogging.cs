// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

namespace Ecl.Core.Logging;

public static class EclLogging
{
    public static Serilog.Core.Logger CreateLogger(
        AppPaths paths,
        bool console = true,
        LogEventLevel consoleMinimumLevel = LogEventLevel.Information)
    {
        var configuration = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.FromLogContext()
            .WriteTo.Debug(restrictedToMinimumLevel: LogEventLevel.Debug)
            .WriteTo.File(
                Path.Combine(paths.LogsDirectory, "ecl-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                restrictedToMinimumLevel: LogEventLevel.Debug,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}");

        if (console)
        {
            configuration.WriteTo.Console(
                restrictedToMinimumLevel: consoleMinimumLevel,
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}");
        }

        return configuration.CreateLogger();
    }

    public static IServiceCollection AddEclLogging(
        this IServiceCollection services,
        AppPaths paths,
        bool console = true,
        LogEventLevel consoleMinimumLevel = LogEventLevel.Information)
    {
        var logger = CreateLogger(paths, console, consoleMinimumLevel);
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddSerilog(logger, dispose: true);
        });

        return services;
    }
}
