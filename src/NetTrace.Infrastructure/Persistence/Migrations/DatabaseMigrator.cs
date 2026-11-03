using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using NetTrace.Core.Time;

namespace NetTrace.Infrastructure.Persistence.Migrations;

public sealed record MigrationResult(int FromVersion, int ToVersion, int AppliedCount);

/// <summary>The database was created by a newer NetTrace version than this one understands.</summary>
public sealed class DatabaseVersionException(int databaseVersion, int supportedVersion)
    : Exception($"The database schema version {databaseVersion} is newer than this version of NetTrace supports ({supportedVersion}). Update NetTrace to open it.")
{
    public int DatabaseVersion { get; } = databaseVersion;

    public int SupportedVersion { get; } = supportedVersion;
}

/// <summary>
/// Applies pending forward-only migrations, each in its own transaction, and records them in
/// <c>SchemaMigrations</c>. It never downgrades or deletes data. A database from a newer app
/// version is refused rather than modified.
/// </summary>
public sealed class DatabaseMigrator
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly IClock _clock;
    private readonly ILogger<DatabaseMigrator> _logger;
    private readonly IReadOnlyList<SchemaMigration> _migrations;

    public DatabaseMigrator(SqliteConnectionFactory connectionFactory, IClock clock, ILogger<DatabaseMigrator> logger)
        : this(connectionFactory, clock, logger, SchemaMigrations.All)
    {
    }

    internal DatabaseMigrator(
        SqliteConnectionFactory connectionFactory,
        IClock clock,
        ILogger<DatabaseMigrator> logger,
        IReadOnlyList<SchemaMigration> migrations)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        ValidateSequence(migrations);

        _connectionFactory = connectionFactory;
        _clock = clock;
        _logger = logger;
        _migrations = migrations;
    }

    public Task<MigrationResult> MigrateAsync(CancellationToken cancellationToken = default) =>
        SqliteRetry.ExecuteAsync(MigrateCoreAsync, cancellationToken);

    private async Task<MigrationResult> MigrateCoreAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        // WAL lets exports read while monitoring writes; the setting persists in the database file.
        await ExecuteAsync(connection, null, "PRAGMA journal_mode = WAL;", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            null,
            """
            CREATE TABLE IF NOT EXISTS SchemaMigrations (
                Version      INTEGER NOT NULL PRIMARY KEY,
                Name         TEXT    NOT NULL,
                AppliedAtUtc TEXT    NOT NULL
            );
            """,
            cancellationToken).ConfigureAwait(false);

        var fromVersion = await GetCurrentVersionAsync(connection, cancellationToken).ConfigureAwait(false);
        var latest = _migrations.Count == 0 ? 0 : _migrations[^1].Version;
        if (fromVersion > latest)
        {
            throw new DatabaseVersionException(fromVersion, latest);
        }

        var applied = 0;
        foreach (var migration in _migrations.Where(m => m.Version > fromVersion))
        {
            await using var transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken)
                .ConfigureAwait(false);

            await ExecuteAsync(connection, transaction, migration.Sql, cancellationToken).ConfigureAwait(false);

            await using (var record = connection.CreateCommand())
            {
                record.Transaction = transaction;
                record.CommandText =
                    "INSERT INTO SchemaMigrations (Version, Name, AppliedAtUtc) VALUES ($version, $name, $appliedAt);";
                record.Parameters.AddWithValue("$version", migration.Version);
                record.Parameters.AddWithValue("$name", migration.Name);
                record.Parameters.AddWithValue("$appliedAt", SqliteTimestamp.ToText(_clock.UtcNow));
                await record.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            applied++;
            _logger.LogInformation(
                "Applied database migration {MigrationVersion} ({MigrationName}).", migration.Version, migration.Name);
        }

        var toVersion = applied == 0 ? fromVersion : latest;
        if (applied == 0)
        {
            _logger.LogInformation("Database schema is current at version {SchemaVersion}.", toVersion);
        }

        return new MigrationResult(fromVersion, toVersion, applied);
    }

    private static async Task<int> GetCurrentVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(Version), 0) FROM SchemaMigrations;";
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateSequence(IReadOnlyList<SchemaMigration> migrations)
    {
        ArgumentNullException.ThrowIfNull(migrations);
        for (var i = 0; i < migrations.Count; i++)
        {
            if (migrations[i].Version != i + 1)
            {
                throw new ArgumentException(
                    $"Migrations must be numbered consecutively from 1; found version {migrations[i].Version} at position {i + 1}.",
                    nameof(migrations));
            }
        }
    }
}
