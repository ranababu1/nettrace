using Microsoft.Extensions.Logging.Abstractions;
using NetTrace.Core.Monitoring;
using NetTrace.Infrastructure.Lifecycle;
using NetTrace.Infrastructure.Persistence;
using NetTrace.Infrastructure.Persistence.Migrations;

namespace NetTrace.Infrastructure.Tests;

public sealed class ApplicationLifecycleServiceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Start = new(2026, 11, 3, 8, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _temp = new();
    private readonly FakeClock _clock = new(Start);
    private SqliteConnectionFactory _factory = null!;
    private SqliteLifecycleStore _store = null!;

    public async Task InitializeAsync()
    {
        _factory = new SqliteConnectionFactory(Path.Combine(_temp.Path, "nettrace.db"));
        await new DatabaseMigrator(_factory, _clock, NullLogger<DatabaseMigrator>.Instance).MigrateAsync();
        _store = new SqliteLifecycleStore(_factory, NullLogger<SqliteLifecycleStore>.Instance);
    }

    public Task DisposeAsync()
    {
        _temp.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task FirstRun_RecordsStartWithoutGap()
    {
        using var service = CreateService();

        await service.StartAsync(CancellationToken.None);

        Assert.Equal(["ApplicationStarted"], await QueryColumnAsync("SELECT EventType FROM ApplicationEvents ORDER BY Id;"));
        Assert.Empty(await QueryColumnAsync("SELECT Reason FROM MonitoringGaps;"));
        Assert.Equal(Start, service.CurrentSession!.StartedAtUtc);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task CleanStopThenRestart_RecordsApplicationNotRunningGap()
    {
        using (var first = CreateService())
        {
            await first.StartAsync(CancellationToken.None);
            _clock.UtcNow = Start.AddMinutes(10);
            await first.StopAsync(CancellationToken.None);
        }

        _clock.UtcNow = Start.AddHours(2);
        using var second = CreateService();
        await second.StartAsync(CancellationToken.None);

        var gap = Assert.Single(await QueryGapsAsync());
        Assert.Equal(("2026-11-03T08:10:00.0000000Z", "2026-11-03T10:00:00.0000000Z", nameof(MonitoringGapReason.ApplicationNotRunning)), gap);
        Assert.Equal(
            ["ApplicationStarted", "ApplicationStopped", "ApplicationStarted"],
            await QueryColumnAsync("SELECT EventType FROM ApplicationEvents ORDER BY Id;"));
        await second.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task CrashThenRestart_RecordsUnexpectedTerminationGapFromLastHeartbeat()
    {
        var crashed = CreateService();
        await crashed.StartAsync(CancellationToken.None);
        _clock.UtcNow = Start.AddMinutes(1);
        await _store.UpdateSessionAsync(crashed.CurrentSession! with { LastAliveAtUtc = _clock.UtcNow });
        // No StopAsync: simulates the process being killed.

        _clock.UtcNow = Start.AddMinutes(45);
        using var restarted = CreateService();
        await restarted.StartAsync(CancellationToken.None);

        var gap = Assert.Single(await QueryGapsAsync());
        Assert.Equal(("2026-11-03T08:01:00.0000000Z", "2026-11-03T08:45:00.0000000Z", nameof(MonitoringGapReason.UnexpectedTermination)), gap);
        Assert.Contains("UncleanShutdownDetected", await QueryColumnAsync("SELECT EventType FROM ApplicationEvents;"));
        await restarted.StopAsync(CancellationToken.None);
        await crashed.StopAsync(CancellationToken.None);
        crashed.Dispose();
    }

    [Fact]
    public async Task UnreadableStoredSession_StartsWithoutInventingAGap()
    {
        await ExecuteAsync("INSERT INTO AppSettings (Key, Value, UpdatedAtUtc) VALUES ('lifecycle.session', '{broken', '2026-11-03T00:00:00.0000000Z');");
        using var service = CreateService();

        await service.StartAsync(CancellationToken.None);

        Assert.Empty(await QueryGapsAsync());
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Stop_PersistsStoppedSession()
    {
        using var service = CreateService();
        await service.StartAsync(CancellationToken.None);
        _clock.UtcNow = Start.AddMinutes(5);

        await service.StopAsync(CancellationToken.None);

        var stored = await _store.LoadSessionAsync();
        Assert.Equal(Start.AddMinutes(5), stored!.StoppedAtUtc);
        Assert.Equal(service.CurrentSession!.SessionId, stored.SessionId);
    }

    private ApplicationLifecycleService CreateService() =>
        new(_store, _clock, NullLogger<ApplicationLifecycleService>.Instance);

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = await _factory.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<List<string>> QueryColumnAsync(string sql)
    {
        await using var connection = await _factory.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private async Task<List<(string Start, string End, string Reason)>> QueryGapsAsync()
    {
        await using var connection = await _factory.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT StartedAtUtc, EndedAtUtc, Reason FROM MonitoringGaps ORDER BY Id;";
        var gaps = new List<(string, string, string)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            gaps.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return gaps;
    }
}
