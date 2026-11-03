using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetTrace.Core.Configuration;
using NetTrace.Core.Time;
using NetTrace.Infrastructure.Configuration;
using NetTrace.Infrastructure.Logging;
using NetTrace.Infrastructure.Persistence;
using NetTrace.Infrastructure.Persistence.Migrations;
using NetTrace.Infrastructure.Storage;
using NetTrace.Infrastructure.Time;

namespace NetTrace.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddNetTraceInfrastructure(this IServiceCollection services, AppDataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(paths);

        services.AddSingleton(paths);
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton(new SqliteConnectionFactory(paths.DatabasePath));
        services.AddSingleton<DatabaseMigrator>();
        services.AddHostedService<DatabaseInitializer>();
        services.AddSingleton<ISettingsRepository, SqliteSettingsRepository>();
        services.AddSingleton<IMonitoringConfigurationStore, MonitoringConfigurationStore>();
        return services;
    }

    /// <summary>Adds the structured JSON-lines file logger writing to <paramref name="logDirectory"/>.</summary>
    public static ILoggingBuilder AddNetTraceFileLogger(
        this ILoggingBuilder builder, string logDirectory, LogLevel minimumLevel = LogLevel.Information)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddSingleton<ILoggerProvider>(sp =>
            new JsonFileLoggerProvider(logDirectory, sp.GetRequiredService<IClock>(), minimumLevel));
        return builder;
    }
}
