using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using NetTrace.Core.Monitoring;
using NetTrace.Infrastructure.Persistence;

namespace NetTrace.Infrastructure.Lifecycle;

/// <summary>
/// Persists the current run's <see cref="LifecycleSession"/> (in <c>AppSettings</c>) together with
/// the application events and monitoring gaps produced by reconciliation, atomically.
/// </summary>
public sealed class SqliteLifecycleStore(SqliteConnectionFactory connectionFactory, ILogger<SqliteLifecycleStore> logger)
{
    public const string SessionKey = "lifecycle.session";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public Task<LifecycleSession?> LoadSessionAsync(CancellationToken cancellationToken = default) =>
        SqliteRetry.ExecuteAsync(
            async ct =>
            {
                await using var connection = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT Value FROM AppSettings WHERE Key = $key;";
                command.Parameters.AddWithValue("$key", SessionKey);
                if (await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is not string json)
                {
                    return null;
                }

                try
                {
                    return JsonSerializer.Deserialize<LifecycleSession>(json, JsonOptions);
                }
                catch (JsonException ex)
                {
                    // Without a readable session the previous run's end is unknown; no gap can be dated.
                    logger.LogWarning(ex, "The stored lifecycle session could not be read and is ignored.");
                    return null;
                }
            },
            cancellationToken);

    public Task ApplyStartupAsync(LifecycleReconciliation reconciliation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reconciliation);
        return InTransactionAsync(
            async (connection, transaction, ct) =>
            {
                if (reconciliation.Gap is { } gap)
                {
                    await InsertGapAsync(connection, transaction, gap, ct).ConfigureAwait(false);
                }

                foreach (var applicationEvent in reconciliation.Events)
                {
                    await InsertEventAsync(connection, transaction, applicationEvent, ct).ConfigureAwait(false);
                }

                await WriteSessionAsync(connection, transaction, reconciliation.NewSession, ct).ConfigureAwait(false);
            },
            cancellationToken);
    }

    public Task UpdateSessionAsync(LifecycleSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        return InTransactionAsync(
            (connection, transaction, ct) => WriteSessionAsync(connection, transaction, session, ct),
            cancellationToken);
    }

    public Task RecordStopAsync(LifecycleSession stoppedSession, ApplicationEvent stopEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stoppedSession);
        ArgumentNullException.ThrowIfNull(stopEvent);
        return InTransactionAsync(
            async (connection, transaction, ct) =>
            {
                await InsertEventAsync(connection, transaction, stopEvent, ct).ConfigureAwait(false);
                await WriteSessionAsync(connection, transaction, stoppedSession, ct).ConfigureAwait(false);
            },
            cancellationToken);
    }

    private Task InTransactionAsync(
        Func<SqliteConnection, SqliteTransaction, CancellationToken, Task> work, CancellationToken cancellationToken) =>
        SqliteRetry.ExecuteAsync(
            async ct =>
            {
                await using var connection = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
                await using var transaction = connection.BeginTransaction();
                await work(connection, transaction, ct).ConfigureAwait(false);
                await transaction.CommitAsync(ct).ConfigureAwait(false);
                return true;
            },
            cancellationToken);

    private static Task WriteSessionAsync(
        SqliteConnection connection, SqliteTransaction transaction, LifecycleSession session, CancellationToken ct) =>
        SqliteSettingsRepository.UpsertAsync(
            connection,
            transaction,
            SessionKey,
            JsonSerializer.Serialize(session, JsonOptions),
            SqliteTimestamp.ToText(session.StoppedAtUtc ?? session.LastAliveAtUtc),
            ct);

    private static async Task InsertGapAsync(
        SqliteConnection connection, SqliteTransaction transaction, MonitoringGap gap, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO MonitoringGaps (StartedAtUtc, EndedAtUtc, Reason, Details, Source)
            VALUES ($started, $ended, $reason, $details, $source);
            """;
        command.Parameters.AddWithValue("$started", SqliteTimestamp.ToText(gap.StartedAtUtc));
        command.Parameters.AddWithValue("$ended", gap.EndedAtUtc is { } ended ? SqliteTimestamp.ToText(ended) : DBNull.Value);
        command.Parameters.AddWithValue("$reason", gap.Reason.ToString());
        command.Parameters.AddWithValue("$details", (object?)gap.Details ?? DBNull.Value);
        command.Parameters.AddWithValue("$source", gap.Source);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task InsertEventAsync(
        SqliteConnection connection, SqliteTransaction transaction, ApplicationEvent applicationEvent, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO ApplicationEvents (OccurredAtUtc, EventType, DetailsJson)
            VALUES ($occurred, $type, $details);
            """;
        command.Parameters.AddWithValue("$occurred", SqliteTimestamp.ToText(applicationEvent.OccurredAtUtc));
        command.Parameters.AddWithValue("$type", applicationEvent.EventType.ToString());
        command.Parameters.AddWithValue(
            "$details",
            applicationEvent.Details is { } details ? JsonSerializer.Serialize(details, JsonOptions) : DBNull.Value);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
