using Microsoft.Data.Sqlite;
using NetTrace.Core.Configuration;
using NetTrace.Core.Time;

namespace NetTrace.Infrastructure.Persistence;

public sealed class SqliteSettingsRepository(SqliteConnectionFactory connectionFactory, IClock clock) : ISettingsRepository
{
    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return SqliteRetry.ExecuteAsync(
            async ct =>
            {
                await using var connection = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT Value FROM AppSettings WHERE Key = $key;";
                command.Parameters.AddWithValue("$key", key);
                var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
                return value is DBNull or null ? null : (string)value;
            },
            cancellationToken);
    }

    public Task<IReadOnlyDictionary<string, string?>> GetAllAsync(CancellationToken cancellationToken = default) =>
        SqliteRetry.ExecuteAsync<IReadOnlyDictionary<string, string?>>(
            async ct =>
            {
                await using var connection = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT Key, Value FROM AppSettings ORDER BY Key;";
                var values = new Dictionary<string, string?>(StringComparer.Ordinal);
                await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    values[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetString(1);
                }

                return values;
            },
            cancellationToken);

    public Task SetManyAsync(IReadOnlyDictionary<string, string?> values, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (var key in values.Keys)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key, nameof(values));
        }

        if (values.Count == 0)
        {
            return Task.CompletedTask;
        }

        return SqliteRetry.ExecuteAsync(
            async ct =>
            {
                await using var connection = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
                await using var transaction = connection.BeginTransaction();
                var updatedAt = SqliteTimestamp.ToText(clock.UtcNow);
                foreach (var (key, value) in values)
                {
                    await UpsertAsync(connection, transaction, key, value, updatedAt, ct).ConfigureAwait(false);
                }

                await transaction.CommitAsync(ct).ConfigureAwait(false);
                return true;
            },
            cancellationToken);
    }

    /// <summary>Upserts one setting inside a caller-owned transaction.</summary>
    internal static async Task UpsertAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string key,
        string? value,
        string updatedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO AppSettings (Key, Value, UpdatedAtUtc) VALUES ($key, $value, $updatedAt)
            ON CONFLICT (Key) DO UPDATE SET Value = excluded.Value, UpdatedAtUtc = excluded.UpdatedAtUtc;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", (object?)value ?? DBNull.Value);
        command.Parameters.AddWithValue("$updatedAt", updatedAtUtc);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
