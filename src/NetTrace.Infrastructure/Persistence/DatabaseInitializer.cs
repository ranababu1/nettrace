using Microsoft.Extensions.Hosting;
using NetTrace.Infrastructure.Persistence.Migrations;

namespace NetTrace.Infrastructure.Persistence;

/// <summary>Brings the database schema up to date when the host starts, before any UI is shown.</summary>
internal sealed class DatabaseInitializer(DatabaseMigrator migrator) : IHostedService
{
    // Run off the caller's thread: host start is awaited from the WPF dispatcher.
    public Task StartAsync(CancellationToken cancellationToken) =>
        Task.Run(() => migrator.MigrateAsync(cancellationToken), cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
