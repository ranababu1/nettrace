using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NetTrace.Core.Incidents;
using NetTrace.Core.Monitoring;
using NetTrace.Core.Network;
using NetTrace.Core.Probes;
using NetTrace.Infrastructure.Persistence;
using NetTrace.Infrastructure.Persistence.Migrations;

namespace NetTrace.Infrastructure.Tests;

public sealed class SqliteRetentionServiceTests : IAsyncLifetime
{
    // "Now" is mid-day on 2026-11-04 (UTC time zone), so 30 days of retention keeps from 2026-10-05.
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _temp = new();
    private readonly FakeClock _clock = new(Now);
    private SqliteConnectionFactory _factory = null!;
    private SqliteMonitoringRepository _monitoring = null!;
    private SqliteIncidentRepository _incidents = null!;
    private SqliteRetentionService _retention = null!;

    public async Task InitializeAsync()
    {
        _factory = new SqliteConnectionFactory(Path.Combine(_temp.Path, "nettrace.db"));
        await new DatabaseMigrator(_factory, _clock, NullLogger<DatabaseMigrator>.Instance).MigrateAsync();
        _monitoring = new SqliteMonitoringRepository(_factory);
        _incidents = new SqliteIncidentRepository(_factory);
        _retention = new SqliteRetentionService(_factory, _monitoring, _incidents, _monitoring, _clock, NullLogger<SqliteRetentionService>.Instance);
    }

    public Task DisposeAsync()
    {
        _temp.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Forever_PreviewsAndDeletesNothing()
    {
        await SaveCycleAsync(Now.AddDays(-400));

        Assert.Equal(RetentionPreviewNothing, await _retention.PreviewAsync(0));
        var result = await _retention.CleanupAsync(0);

        Assert.Equal(0, result.ObservationsDeleted);
        Assert.Equal(2L, await ScalarAsync("SELECT COUNT(*) FROM NetworkObservations;"));
    }

    [Fact]
    public async Task Preview_CountsOnlyRowsBeforeTheCutoff_WithoutDeleting()
    {
        await SaveCycleAsync(Now.AddDays(-40));
        await SaveCycleAsync(Now.AddDays(-2));

        var preview = await _retention.PreviewAsync(30);

        Assert.Equal(new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero), preview.CutoffUtc);
        Assert.Equal(2, preview.ObservationCount); // two probes per cycle, one old cycle
        Assert.Equal(2L + 2, await ScalarAsync("SELECT COUNT(*) FROM NetworkObservations;"));
    }

    [Fact]
    public async Task Cleanup_SavesDailySummaryFirst_ThenDeletesOldRowsOnly()
    {
        var oldDay = Now.AddDays(-40);
        await SaveCycleAsync(oldDay);
        await SaveCycleAsync(Now.AddDays(-2));
        await _incidents.SaveAsync(new Incident(Guid.NewGuid(), oldDay, oldDay.AddSeconds(15), oldDay.AddMinutes(10),
            IncidentClassification.GatewayUnreachable, ClassificationConfidence.High, "s", [], []));
        await _monitoring.AddAsync(new MonitoringGap(oldDay.AddHours(1), oldDay.AddHours(2), MonitoringGapReason.SystemSleep, null, "t"));

        var result = await _retention.CleanupAsync(30);

        Assert.Equal(2, result.ObservationsDeleted);
        Assert.Equal(1, result.DaysSummarised);
        Assert.Equal(2L, await ScalarAsync("SELECT COUNT(*) FROM NetworkObservations;"));
        Assert.Equal(1L, await ScalarAsync("SELECT COUNT(*) FROM DailyStatistics;"));
        Assert.Equal(1L, await ScalarAsync("SELECT IncidentCount FROM DailyStatistics;"));
        Assert.Equal(3600.0, Convert.ToDouble(await ScalarAsync("SELECT UnmonitoredSeconds FROM DailyStatistics;")));
        // Incidents and gaps are never removed.
        Assert.Equal(1L, await ScalarAsync("SELECT COUNT(*) FROM OutageEvents;"));
        Assert.Equal(1L, await ScalarAsync("SELECT COUNT(*) FROM MonitoringGaps;"));

        // Running again is a no-op.
        var again = await _retention.CleanupAsync(30);
        Assert.Equal(0, again.ObservationsDeleted);
        Assert.Equal(1L, await ScalarAsync("SELECT COUNT(*) FROM DailyStatistics;"));
    }

    private static readonly NetTrace.Core.Retention.RetentionPreview RetentionPreviewNothing = NetTrace.Core.Retention.RetentionPreview.Nothing;

    private Task SaveCycleAsync(DateTimeOffset at)
    {
        ProbeResult Result(ProbeType type, string target) =>
            new(type, target, "m", at, at.AddMilliseconds(5), ProbeOutcome.Success, 5, 1000, null, null);
        var cycle = new MonitoringCycle(
            Guid.NewGuid(), at, at, at.AddMilliseconds(5), 1, new NetworkContext(at, [], null, null),
            [Result(ProbeType.Gateway, "192.168.1.1"), Result(ProbeType.ExternalIcmp, "1.1.1.1")]);
        return _monitoring.SaveCycleAsync(cycle, recordNetworkSnapshot: false);
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var connection = await _factory.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }
}
