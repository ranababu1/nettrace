using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NetTrace.Core.Monitoring;
using NetTrace.Core.Probes;

namespace NetTrace.Infrastructure.Persistence;

/// <summary>SQLite storage for observations, network snapshots and monitoring gaps.</summary>
public sealed class SqliteMonitoringRepository(SqliteConnectionFactory connectionFactory)
    : IObservationRepository, IMonitoringGapRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public Task SaveCycleAsync(MonitoringCycle cycle, bool recordNetworkSnapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cycle);
        return SqliteRetry.ExecuteAsync(
            async ct =>
            {
                await using var connection = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
                await using var transaction = connection.BeginTransaction();

                if (recordNetworkSnapshot)
                {
                    await InsertSnapshotAsync(connection, transaction, cycle, ct).ConfigureAwait(false);
                }

                await using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO NetworkObservations (
                        ObservedAtUtc, CompletedAtUtc, CycleId, ProbeType, Target, Method, Success, TimedOut,
                        LatencyMs, DurationMs, TimeoutMs, ErrorCode, ErrorMessage, InterfaceId, Classification,
                        ConfigurationVersion, Outcome)
                    VALUES (
                        $observedAt, $completedAt, $cycleId, $probeType, $target, $method, $success, $timedOut,
                        $latency, $duration, $timeout, $errorCode, $errorMessage, $interfaceId, NULL,
                        $configVersion, $outcome);
                    """;
                var observedAt = insert.Parameters.Add("$observedAt", SqliteType.Text);
                var completedAt = insert.Parameters.Add("$completedAt", SqliteType.Text);
                var cycleId = insert.Parameters.Add("$cycleId", SqliteType.Text);
                var probeType = insert.Parameters.Add("$probeType", SqliteType.Text);
                var target = insert.Parameters.Add("$target", SqliteType.Text);
                var method = insert.Parameters.Add("$method", SqliteType.Text);
                var success = insert.Parameters.Add("$success", SqliteType.Integer);
                var timedOut = insert.Parameters.Add("$timedOut", SqliteType.Integer);
                var latency = insert.Parameters.Add("$latency", SqliteType.Real);
                var duration = insert.Parameters.Add("$duration", SqliteType.Real);
                var timeout = insert.Parameters.Add("$timeout", SqliteType.Integer);
                var errorCode = insert.Parameters.Add("$errorCode", SqliteType.Text);
                var errorMessage = insert.Parameters.Add("$errorMessage", SqliteType.Text);
                var interfaceId = insert.Parameters.Add("$interfaceId", SqliteType.Text);
                var configVersion = insert.Parameters.Add("$configVersion", SqliteType.Integer);
                var outcome = insert.Parameters.Add("$outcome", SqliteType.Text);

                cycleId.Value = cycle.CycleId.ToString("D");
                interfaceId.Value = (object?)cycle.NetworkContext.ActiveInterface?.Id ?? DBNull.Value;
                configVersion.Value = cycle.ConfigurationVersion;

                foreach (var result in cycle.Results)
                {
                    observedAt.Value = SqliteTimestamp.ToText(result.StartedAtUtc);
                    completedAt.Value = SqliteTimestamp.ToText(result.CompletedAtUtc);
                    probeType.Value = result.ProbeType.ToString();
                    target.Value = result.Target;
                    method.Value = result.Method;
                    success.Value = result.Succeeded ? 1 : 0;
                    timedOut.Value = result.TimedOut ? 1 : 0;
                    latency.Value = (object?)result.LatencyMs ?? DBNull.Value;
                    duration.Value = result.DurationMs;
                    timeout.Value = result.TimeoutMilliseconds;
                    errorCode.Value = (object?)result.ErrorCode ?? DBNull.Value;
                    errorMessage.Value = (object?)result.ErrorMessage ?? DBNull.Value;
                    outcome.Value = result.Outcome.ToString();
                    await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                await transaction.CommitAsync(ct).ConfigureAwait(false);
                return true;
            },
            cancellationToken);
    }

    public Task<IReadOnlyList<StoredObservation>> GetObservationsAsync(
        DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken = default) =>
        SqliteRetry.ExecuteAsync<IReadOnlyList<StoredObservation>>(
            async ct =>
            {
                await using var connection = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT Id, CycleId, ConfigurationVersion, InterfaceId, ProbeType, Target, Method, ObservedAtUtc,
                           CompletedAtUtc, Success, TimedOut, LatencyMs, TimeoutMs, ErrorCode, ErrorMessage, Outcome
                    FROM NetworkObservations
                    WHERE ObservedAtUtc >= $from AND ObservedAtUtc < $to
                    ORDER BY ObservedAtUtc, Id;
                    """;
                command.Parameters.AddWithValue("$from", SqliteTimestamp.ToText(fromUtc));
                command.Parameters.AddWithValue("$to", SqliteTimestamp.ToText(toUtc));

                var observations = new List<StoredObservation>();
                await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var startedAt = SqliteTimestamp.Parse(reader.GetString(7));
                    var result = new ProbeResult(
                        Enum.Parse<ProbeType>(reader.GetString(4)),
                        reader.GetString(5),
                        reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                        startedAt,
                        reader.IsDBNull(8) ? startedAt : SqliteTimestamp.Parse(reader.GetString(8)),
                        ReadOutcome(reader.IsDBNull(15) ? null : reader.GetString(15), reader.GetInt32(9) == 1, reader.GetInt32(10) == 1),
                        reader.IsDBNull(11) ? null : reader.GetDouble(11),
                        reader.GetInt32(12),
                        reader.IsDBNull(13) ? null : reader.GetString(13),
                        reader.IsDBNull(14) ? null : reader.GetString(14));
                    observations.Add(new StoredObservation(
                        reader.GetInt64(0),
                        Guid.Parse(reader.GetString(1)),
                        reader.GetInt32(2),
                        reader.IsDBNull(3) ? null : reader.GetString(3),
                        result));
                }

                return observations;
            },
            cancellationToken);

    public Task<long> AddAsync(MonitoringGap gap, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gap);
        return SqliteRetry.ExecuteAsync(
            async ct =>
            {
                await using var connection = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO MonitoringGaps (StartedAtUtc, EndedAtUtc, Reason, Details, Source)
                    VALUES ($started, $ended, $reason, $details, $source)
                    RETURNING Id;
                    """;
                command.Parameters.AddWithValue("$started", SqliteTimestamp.ToText(gap.StartedAtUtc));
                command.Parameters.AddWithValue("$ended", gap.EndedAtUtc is { } ended ? SqliteTimestamp.ToText(ended) : DBNull.Value);
                command.Parameters.AddWithValue("$reason", gap.Reason.ToString());
                command.Parameters.AddWithValue("$details", (object?)gap.Details ?? DBNull.Value);
                command.Parameters.AddWithValue("$source", gap.Source);
                return Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
            },
            cancellationToken);
    }

    public Task CloseAsync(long gapId, DateTimeOffset endedAtUtc, CancellationToken cancellationToken = default) =>
        SqliteRetry.ExecuteAsync(
            async ct =>
            {
                await using var connection = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
                await using var command = connection.CreateCommand();
                command.CommandText = "UPDATE MonitoringGaps SET EndedAtUtc = $ended WHERE Id = $id AND EndedAtUtc IS NULL;";
                command.Parameters.AddWithValue("$ended", SqliteTimestamp.ToText(endedAtUtc));
                command.Parameters.AddWithValue("$id", gapId);
                return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            },
            cancellationToken);

    public Task<IReadOnlyList<MonitoringGap>> GetOverlappingAsync(
        DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken = default) =>
        SqliteRetry.ExecuteAsync<IReadOnlyList<MonitoringGap>>(
            async ct =>
            {
                await using var connection = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT StartedAtUtc, EndedAtUtc, Reason, Details, Source
                    FROM MonitoringGaps
                    WHERE StartedAtUtc < $to AND (EndedAtUtc IS NULL OR EndedAtUtc > $from)
                    ORDER BY StartedAtUtc, Id;
                    """;
                command.Parameters.AddWithValue("$from", SqliteTimestamp.ToText(fromUtc));
                command.Parameters.AddWithValue("$to", SqliteTimestamp.ToText(toUtc));

                var gaps = new List<MonitoringGap>();
                await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    gaps.Add(new MonitoringGap(
                        SqliteTimestamp.Parse(reader.GetString(0)),
                        reader.IsDBNull(1) ? null : SqliteTimestamp.Parse(reader.GetString(1)),
                        Enum.TryParse<MonitoringGapReason>(reader.GetString(2), out var reason) ? reason : MonitoringGapReason.Unknown,
                        reader.IsDBNull(3) ? null : reader.GetString(3),
                        reader.GetString(4)));
                }

                return gaps;
            },
            cancellationToken);

    // Rows from before schema v2 have no Outcome; Success/TimedOut is all that is known about them.
    private static ProbeOutcome ReadOutcome(string? outcome, bool success, bool timedOut) =>
        Enum.TryParse<ProbeOutcome>(outcome, out var parsed) ? parsed
        : success ? ProbeOutcome.Success
        : timedOut ? ProbeOutcome.Timeout
        : ProbeOutcome.Failure;

    private static async Task InsertSnapshotAsync(
        SqliteConnection connection, SqliteTransaction transaction, MonitoringCycle cycle, CancellationToken ct)
    {
        var context = cycle.NetworkContext;
        var nic = context.ActiveInterface;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO NetworkSnapshots (
                CapturedAtUtc, InterfaceId, InterfaceName, InterfaceType, OperationalStatus, Ipv4AddressesJson,
                GatewayAddress, DnsServersJson, Ssid, WifiSignalQuality, MetadataJson)
            VALUES ($captured, $id, $name, $type, $status, $ipv4, $gateway, $dns, NULL, NULL, $metadata);
            """;
        command.Parameters.AddWithValue("$captured", SqliteTimestamp.ToText(context.CapturedAtUtc));
        command.Parameters.AddWithValue("$id", (object?)nic?.Id ?? DBNull.Value);
        command.Parameters.AddWithValue("$name", (object?)nic?.Name ?? DBNull.Value);
        command.Parameters.AddWithValue("$type", (object?)nic?.Kind.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", (object?)nic?.Status.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$ipv4", nic is null ? DBNull.Value : JsonSerializer.Serialize(nic.Ipv4Addresses, JsonOptions));
        command.Parameters.AddWithValue("$gateway", (object?)context.DefaultGateway ?? DBNull.Value);
        command.Parameters.AddWithValue("$dns", nic is null ? DBNull.Value : JsonSerializer.Serialize(nic.DnsServers, JsonOptions));
        command.Parameters.AddWithValue("$metadata", JsonSerializer.Serialize(
            new SnapshotMetadata(nic is not null, nic?.Description, context.Interfaces.Count),
            JsonOptions));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private sealed record SnapshotMetadata(bool ActiveConnection, string? InterfaceDescription, int InterfaceCount);
}
