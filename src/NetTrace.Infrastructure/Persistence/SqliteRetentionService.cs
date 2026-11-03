using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using NetTrace.Core.Incidents;
using NetTrace.Core.Monitoring;
using NetTrace.Core.Retention;
using NetTrace.Core.Statistics;
using NetTrace.Core.Time;

namespace NetTrace.Infrastructure.Persistence;

public sealed class SqliteRetentionService(
    SqliteConnectionFactory connectionFactory,
    IObservationRepository observations,
    IIncidentRepository incidents,
    IMonitoringGapRepository gaps,
    IClock clock,
    ILogger<SqliteRetentionService> logger) : IRetentionService
{
    /// <summary>Whole local days older than <paramref name="retentionDays"/> days ago; null for Forever.</summary>
    internal DateTimeOffset? Cutoff(int retentionDays)
    {
        if (retentionDays <= 0)
        {
            return null;
        }

        var zone = clock.LocalTimeZone;
        var localToday = TimeZoneInfo.ConvertTime(clock.UtcNow, zone).Date;
        var cutoffLocal = DateTime.SpecifyKind(localToday.AddDays(-retentionDays), DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(cutoffLocal, zone), TimeSpan.Zero);
    }

    public Task<RetentionPreview> PreviewAsync(int retentionDays, CancellationToken cancellationToken = default)
    {
        if (Cutoff(retentionDays) is not { } cutoff)
        {
            return Task.FromResult(RetentionPreview.Nothing);
        }

        return SqliteRetry.ExecuteAsync(
            async ct =>
            {
                await using var connection = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
                return new RetentionPreview(
                    cutoff,
                    await CountAsync(connection, "NetworkObservations", "ObservedAtUtc", cutoff, ct).ConfigureAwait(false),
                    await CountAsync(connection, "NetworkSnapshots", "CapturedAtUtc", cutoff, ct).ConfigureAwait(false));
            },
            cancellationToken);
    }

    public async Task<RetentionResult> CleanupAsync(int retentionDays, CancellationToken cancellationToken = default)
    {
        if (Cutoff(retentionDays) is not { } cutoff)
        {
            return new RetentionResult(null, 0, 0, 0);
        }

        var summarised = await SummariseDaysBeforeAsync(cutoff, cancellationToken).ConfigureAwait(false);

        var (obsDeleted, snapDeleted) = await SqliteRetry.ExecuteAsync(
            async ct =>
            {
                await using var connection = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
                await using var transaction = connection.BeginTransaction();
                var a = await DeleteAsync(connection, transaction, "NetworkObservations", "ObservedAtUtc", cutoff, ct).ConfigureAwait(false);
                var b = await DeleteAsync(connection, transaction, "NetworkSnapshots", "CapturedAtUtc", cutoff, ct).ConfigureAwait(false);
                transaction.Commit();
                return (a, b);
            },
            cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "Retention cleanup removed {Observations} observations and {Snapshots} network snapshots older than {Cutoff:o}; {Days} daily summaries saved first.",
            obsDeleted, snapDeleted, cutoff, summarised);
        return new RetentionResult(cutoff, obsDeleted, snapDeleted, summarised);
    }

    private async Task<int> SummariseDaysBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        var oldest = await SqliteRetry.ExecuteAsync(
            async ct =>
            {
                await using var connection = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT MIN(ObservedAtUtc) FROM NetworkObservations WHERE ObservedAtUtc < $cutoff;";
                command.Parameters.AddWithValue("$cutoff", SqliteTimestamp.ToText(cutoff));
                return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
            },
            cancellationToken).ConfigureAwait(false);
        if (oldest is null)
        {
            return 0;
        }

        var zone = clock.LocalTimeZone;
        var day = TimeZoneInfo.ConvertTime(SqliteTimestamp.Parse(oldest), zone).Date;
        var count = 0;
        while (true)
        {
            var start = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(day, DateTimeKind.Unspecified), zone), TimeSpan.Zero);
            if (start >= cutoff)
            {
                return count;
            }

            var end = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(day.AddDays(1), DateTimeKind.Unspecified), zone), TimeSpan.Zero);
            var dayObservations = await observations.GetObservationsAsync(start, end, cancellationToken).ConfigureAwait(false);
            if (dayObservations.Count > 0)
            {
                var dayIncidents = await incidents.GetOverlappingAsync(start, end, cancellationToken).ConfigureAwait(false);
                var dayGaps = await gaps.GetOverlappingAsync(start, end, cancellationToken).ConfigureAwait(false);
                var stats = DailyStatisticsCalculator.Calculate(start, end, clock.UtcNow, dayObservations, dayIncidents, dayGaps);
                await SaveDailyAsync(day, zone.Id, stats, cancellationToken).ConfigureAwait(false);
                count++;
            }

            day = day.AddDays(1);
        }
    }

    private Task SaveDailyAsync(DateTime localDate, string timeZoneId, DailyStatistics s, CancellationToken cancellationToken) =>
        SqliteRetry.ExecuteAsync(
            async ct =>
            {
                await using var connection = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
                await using var command = connection.CreateCommand();
                // Per-target latency, loss and jitter are not representable in this table, so they stay NULL
                // (not measured here) and exist only while the raw observations do.
                command.CommandText = """
                    INSERT INTO DailyStatistics (
                        DateLocal, TimeZoneId, ObservedSeconds, HealthySeconds, DegradedSeconds, OutageSeconds, UnmonitoredSeconds,
                        UptimePercentage, AverageLatencyMs, MedianLatencyMs, P95LatencyMs, MaximumLatencyMs, AverageJitterMs,
                        PacketLossPercentage, IncidentCount, LongestIncidentSeconds, LastCalculatedAtUtc)
                    VALUES (
                        $date, $zone, $observed, $healthy, 0, $outage, $unmonitored,
                        $uptime, NULL, NULL, NULL, NULL, NULL,
                        NULL, $incidents, $longest, $calculated)
                    ON CONFLICT (DateLocal, TimeZoneId) DO UPDATE SET
                        ObservedSeconds = excluded.ObservedSeconds, HealthySeconds = excluded.HealthySeconds,
                        OutageSeconds = excluded.OutageSeconds, UnmonitoredSeconds = excluded.UnmonitoredSeconds,
                        UptimePercentage = excluded.UptimePercentage, IncidentCount = excluded.IncidentCount,
                        LongestIncidentSeconds = excluded.LongestIncidentSeconds, LastCalculatedAtUtc = excluded.LastCalculatedAtUtc;
                    """;
                command.Parameters.AddWithValue("$date", localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$zone", timeZoneId);
                command.Parameters.AddWithValue("$observed", s.ObservedSeconds);
                command.Parameters.AddWithValue("$healthy", s.HealthySeconds);
                command.Parameters.AddWithValue("$outage", s.OutageSeconds);
                command.Parameters.AddWithValue("$unmonitored", s.UnmonitoredSeconds);
                command.Parameters.AddWithValue("$uptime", s.UptimePercentage is { } u ? u : DBNull.Value);
                command.Parameters.AddWithValue("$incidents", s.IncidentCount);
                command.Parameters.AddWithValue("$longest", s.LongestIncidentSeconds is { } l ? l : DBNull.Value);
                command.Parameters.AddWithValue("$calculated", SqliteTimestamp.ToText(clock.UtcNow));
                return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            },
            cancellationToken);

    private static async Task<int> CountAsync(SqliteConnection connection, string table, string column, DateTimeOffset cutoff, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE {column} < $cutoff;";
        command.Parameters.AddWithValue("$cutoff", SqliteTimestamp.ToText(cutoff));
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private static async Task<int> DeleteAsync(
        SqliteConnection connection, SqliteTransaction transaction, string table, string column, DateTimeOffset cutoff, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"DELETE FROM {table} WHERE {column} < $cutoff;";
        command.Parameters.AddWithValue("$cutoff", SqliteTimestamp.ToText(cutoff));
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
