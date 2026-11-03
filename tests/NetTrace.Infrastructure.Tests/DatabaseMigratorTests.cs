using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NetTrace.Infrastructure.Persistence;
using NetTrace.Infrastructure.Persistence.Migrations;

namespace NetTrace.Infrastructure.Tests;

public sealed class DatabaseMigratorTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 9, 30, 0, TimeSpan.Zero);

    private readonly TempDirectory _temp = new();
    private readonly SqliteConnectionFactory _factory;
    private readonly FakeClock _clock = new(Now);

    public DatabaseMigratorTests()
    {
        _factory = new SqliteConnectionFactory(Path.Combine(_temp.Path, "nettrace.db"));
    }

    [Fact]
    public async Task Migrate_EmptyDatabase_CreatesSchemaTablesAndIndexes()
    {
        var result = await CreateMigrator().MigrateAsync();

        Assert.Equal(new MigrationResult(0, SchemaMigrations.LatestVersion, SchemaMigrations.All.Count), result);
        var tables = await QueryNamesAsync("table");
        Assert.Superset(
            new HashSet<string>
            {
                "SchemaMigrations", "NetworkObservations", "NetworkSnapshots", "OutageEvents",
                "MonitoringGaps", "DailyStatistics", "ApplicationEvents", "AppSettings",
            },
            tables.ToHashSet());
        var indexes = await QueryNamesAsync("index");
        Assert.Contains("IX_NetworkObservations_ObservedAtUtc", indexes);
        Assert.Contains("IX_NetworkObservations_ProbeType_Target_ObservedAtUtc", indexes);
        Assert.Contains("IX_OutageEvents_StartedAtUtc_EndedAtUtc", indexes);
        Assert.Contains("IX_MonitoringGaps_StartedAtUtc_EndedAtUtc", indexes);
    }

    [Fact]
    public async Task Migrate_RecordsAppliedMigrationWithUtcTimestamp()
    {
        await CreateMigrator().MigrateAsync();

        await using var connection = await _factory.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Version, Name, AppliedAtUtc FROM SchemaMigrations ORDER BY Version;";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal("InitialSchema", reader.GetString(1));
        Assert.Equal("2026-11-03T09:30:00.0000000Z", reader.GetString(2));
    }

    [Fact]
    public async Task Migrate_SecondRun_AppliesNothingAndPreservesData()
    {
        await CreateMigrator().MigrateAsync();
        await ExecuteAsync("INSERT INTO AppSettings (Key, Value, UpdatedAtUtc) VALUES ('k', 'v', '2026-11-03T09:30:00.0000000Z');");

        var result = await CreateMigrator().MigrateAsync();

        Assert.Equal(0, result.AppliedCount);
        Assert.Equal(SchemaMigrations.LatestVersion, result.ToVersion);
        Assert.Equal("v", await ScalarAsync("SELECT Value FROM AppSettings WHERE Key = 'k';"));
    }

    [Fact]
    public async Task Migrate_AppliesOnlyPendingMigrations()
    {
        var v1 = new SchemaMigration(1, "One", "CREATE TABLE T1 (Id INTEGER PRIMARY KEY);");
        var v2 = new SchemaMigration(2, "Two", "CREATE TABLE T2 (Id INTEGER PRIMARY KEY);");
        await CreateMigrator([v1]).MigrateAsync();
        await ExecuteAsync("INSERT INTO T1 (Id) VALUES (42);");

        var result = await CreateMigrator([v1, v2]).MigrateAsync();

        Assert.Equal(new MigrationResult(1, 2, 1), result);
        Assert.Contains("T2", await QueryNamesAsync("table"));
        Assert.Equal(42L, await ScalarAsync("SELECT Id FROM T1;"));
    }

    [Fact]
    public async Task Migrate_FailingMigration_RollsBackAndKeepsPreviousVersion()
    {
        var v1 = new SchemaMigration(1, "One", "CREATE TABLE T1 (Id INTEGER PRIMARY KEY);");
        var broken = new SchemaMigration(2, "Broken", "CREATE TABLE T2 (Id INTEGER PRIMARY KEY); THIS IS NOT SQL;");
        await CreateMigrator([v1]).MigrateAsync();

        await Assert.ThrowsAsync<SqliteException>(() => CreateMigrator([v1, broken]).MigrateAsync());

        Assert.Equal(1L, await ScalarAsync("SELECT MAX(Version) FROM SchemaMigrations;"));
        Assert.DoesNotContain("T2", await QueryNamesAsync("table"));
    }

    [Fact]
    public async Task Migrate_DatabaseFromNewerVersion_IsRefusedUnchanged()
    {
        await CreateMigrator().MigrateAsync();
        await ExecuteAsync("INSERT INTO SchemaMigrations (Version, Name, AppliedAtUtc) VALUES (999, 'Future', '2030-01-01T00:00:00.0000000Z');");

        var ex = await Assert.ThrowsAsync<DatabaseVersionException>(() => CreateMigrator().MigrateAsync());

        Assert.Equal(999, ex.DatabaseVersion);
        Assert.Equal(SchemaMigrations.LatestVersion, ex.SupportedVersion);
        Assert.Equal(999L, await ScalarAsync("SELECT MAX(Version) FROM SchemaMigrations;"));
    }

    [Fact]
    public async Task Migrate_EnablesWriteAheadLogging()
    {
        await CreateMigrator().MigrateAsync();

        Assert.Equal("wal", await ScalarAsync("PRAGMA journal_mode;"));
    }

    [Theory]
    [InlineData(new[] { 2 })]
    [InlineData(new[] { 1, 3 })]
    [InlineData(new[] { 1, 1 })]
    public void Constructor_RejectsNonConsecutiveVersions(int[] versions)
    {
        var migrations = versions.Select(v => new SchemaMigration(v, $"M{v}", "SELECT 1;")).ToList();

        Assert.Throws<ArgumentException>(() => CreateMigrator(migrations));
    }

    [Fact]
    public void ShippedMigrations_AreConsecutive()
    {
        var exception = Record.Exception(() => CreateMigrator());

        Assert.Null(exception);
    }

    public void Dispose() => _temp.Dispose();

    private DatabaseMigrator CreateMigrator(IReadOnlyList<SchemaMigration>? migrations = null) =>
        migrations is null
            ? new DatabaseMigrator(_factory, _clock, NullLogger<DatabaseMigrator>.Instance)
            : new DatabaseMigrator(_factory, _clock, NullLogger<DatabaseMigrator>.Instance, migrations);

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = await _factory.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var connection = await _factory.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private async Task<List<string>> QueryNamesAsync(string type)
    {
        await using var connection = await _factory.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = $type;";
        command.Parameters.AddWithValue("$type", type);
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }
}
