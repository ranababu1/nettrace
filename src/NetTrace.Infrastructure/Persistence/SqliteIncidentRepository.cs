using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NetTrace.Core.Incidents;

namespace NetTrace.Infrastructure.Persistence;

/// <summary>SQLite storage for confirmed incidents, in the <c>OutageEvents</c> table.</summary>
public sealed class SqliteIncidentRepository(SqliteConnectionFactory connectionFactory) : IIncidentRepository
{
    private sealed record Evidence(string Summary, IReadOnlyList<string> FailedProbes, IReadOnlyList<string> WorkingProbes);

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public Task SaveAsync(Incident incident, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(incident);
        return SqliteRetry.ExecuteAsync(
            async ct =>
            {
                await using var connection = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO OutageEvents (
                        IncidentId, StartedAtUtc, DetectedAtUtc, EndedAtUtc, DurationSeconds,
                        Classification, Confidence, EvidenceJson, IsConfirmed)
                    VALUES ($id, $started, $detected, $ended, $duration, $classification, $confidence, $evidence, 1)
                    ON CONFLICT (IncidentId) DO UPDATE SET
                        EndedAtUtc = excluded.EndedAtUtc,
                        DurationSeconds = excluded.DurationSeconds,
                        Classification = excluded.Classification,
                        Confidence = excluded.Confidence,
                        EvidenceJson = excluded.EvidenceJson;
                    """;
                command.Parameters.AddWithValue("$id", incident.Id.ToString("D"));
                command.Parameters.AddWithValue("$started", SqliteTimestamp.ToText(incident.StartedAtUtc));
                command.Parameters.AddWithValue("$detected", SqliteTimestamp.ToText(incident.DetectedAtUtc));
                command.Parameters.AddWithValue("$ended", incident.EndedAtUtc is { } e ? SqliteTimestamp.ToText(e) : DBNull.Value);
                command.Parameters.AddWithValue(
                    "$duration",
                    incident.EndedAtUtc is { } end ? (end - incident.StartedAtUtc).TotalSeconds : DBNull.Value);
                command.Parameters.AddWithValue("$classification", incident.Classification.ToString());
                command.Parameters.AddWithValue("$confidence", incident.Confidence.ToString());
                command.Parameters.AddWithValue(
                    "$evidence",
                    JsonSerializer.Serialize(new Evidence(incident.Summary, incident.FailedProbes, incident.WorkingProbes), JsonOptions));
                return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            },
            cancellationToken);
    }

    public Task<int> CloseOrphanedAsync(CancellationToken cancellationToken = default) =>
        SqliteRetry.ExecuteAsync(
            async ct =>
            {
                await using var connection = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
                await using var transaction = connection.BeginTransaction();

                string? latestObservation;
                await using (var latest = connection.CreateCommand())
                {
                    latest.Transaction = transaction;
                    latest.CommandText = "SELECT MAX(ObservedAtUtc) FROM NetworkObservations;";
                    latestObservation = (await latest.ExecuteScalarAsync(ct).ConfigureAwait(false)) as string;
                }

                var open = new List<(long Id, string Started, string Detected)>();
                await using (var select = connection.CreateCommand())
                {
                    select.Transaction = transaction;
                    select.CommandText = "SELECT Id, StartedAtUtc, DetectedAtUtc FROM OutageEvents WHERE EndedAtUtc IS NULL;";
                    await using var reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        open.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
                    }
                }

                foreach (var (id, started, detected) in open)
                {
                    // Fixed-width ISO text sorts chronologically, so the later of the two is the max.
                    var end = latestObservation is not null && string.CompareOrdinal(latestObservation, detected) > 0
                        ? latestObservation
                        : detected;
                    await using var update = connection.CreateCommand();
                    update.Transaction = transaction;
                    update.CommandText = "UPDATE OutageEvents SET EndedAtUtc = $ended, DurationSeconds = $duration WHERE Id = $id;";
                    update.Parameters.AddWithValue("$ended", end);
                    update.Parameters.AddWithValue(
                        "$duration",
                        (SqliteTimestamp.Parse(end) - SqliteTimestamp.Parse(started)).TotalSeconds);
                    update.Parameters.AddWithValue("$id", id);
                    await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                transaction.Commit();
                return open.Count;
            },
            cancellationToken);

    public Task<IReadOnlyList<Incident>> GetOverlappingAsync(
        DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken = default) =>
        SqliteRetry.ExecuteAsync<IReadOnlyList<Incident>>(
            async ct =>
            {
                await using var connection = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT IncidentId, StartedAtUtc, DetectedAtUtc, EndedAtUtc, Classification, Confidence, EvidenceJson
                    FROM OutageEvents
                    WHERE IsConfirmed = 1 AND StartedAtUtc < $to AND (EndedAtUtc IS NULL OR EndedAtUtc > $from)
                    ORDER BY StartedAtUtc, Id;
                    """;
                command.Parameters.AddWithValue("$from", SqliteTimestamp.ToText(fromUtc));
                command.Parameters.AddWithValue("$to", SqliteTimestamp.ToText(toUtc));

                var incidents = new List<Incident>();
                await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var evidence = JsonSerializer.Deserialize<Evidence>(reader.GetString(6), JsonOptions)
                        ?? new Evidence(string.Empty, [], []);
                    incidents.Add(new Incident(
                        reader.IsDBNull(0) ? Guid.Empty : Guid.Parse(reader.GetString(0)),
                        SqliteTimestamp.Parse(reader.GetString(1)),
                        SqliteTimestamp.Parse(reader.GetString(2)),
                        reader.IsDBNull(3) ? null : SqliteTimestamp.Parse(reader.GetString(3)),
                        Enum.Parse<IncidentClassification>(reader.GetString(4)),
                        Enum.Parse<ClassificationConfidence>(reader.GetString(5)),
                        evidence.Summary,
                        evidence.FailedProbes,
                        evidence.WorkingProbes));
                }

                return incidents;
            },
            cancellationToken);
}
