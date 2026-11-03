using Microsoft.Data.Sqlite;
using NetTrace.Infrastructure.Persistence;

namespace NetTrace.Infrastructure.Tests;

public class SqliteSupportTests
{
    [Fact]
    public async Task Retry_TransientErrorThenSuccess_ReturnsResult()
    {
        var attempts = 0;

        var result = await SqliteRetry.ExecuteAsync(
            _ =>
            {
                attempts++;
                return attempts < 3 ? throw new SqliteException("busy", 5) : Task.FromResult("ok");
            },
            baseDelay: TimeSpan.Zero);

        Assert.Equal("ok", result);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task Retry_StopsAfterMaxAttempts()
    {
        var attempts = 0;

        var ex = await Assert.ThrowsAsync<SqliteException>(() => SqliteRetry.ExecuteAsync<int>(
            _ =>
            {
                attempts++;
                throw new SqliteException("locked", 6);
            },
            maxAttempts: 3,
            baseDelay: TimeSpan.Zero));

        Assert.Equal(6, ex.SqliteErrorCode);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task Retry_NonTransientError_IsNotRetried()
    {
        var attempts = 0;

        await Assert.ThrowsAsync<SqliteException>(() => SqliteRetry.ExecuteAsync<int>(
            _ =>
            {
                attempts++;
                throw new SqliteException("constraint", 19);
            },
            baseDelay: TimeSpan.Zero));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public void Timestamp_RoundTripsAsFixedWidthUtc()
    {
        var local = new DateTimeOffset(2026, 11, 3, 14, 5, 6, TimeSpan.FromHours(5.5)).AddTicks(1234567);

        var text = SqliteTimestamp.ToText(local);

        Assert.Equal("2026-11-03T08:35:06.1234567Z", text);
        Assert.Equal(local, SqliteTimestamp.Parse(text));
        Assert.Equal(TimeSpan.Zero, SqliteTimestamp.Parse(text).Offset);
    }

    [Fact]
    public void Timestamp_TextOrderMatchesChronologicalOrder()
    {
        var earlier = SqliteTimestamp.ToText(new DateTimeOffset(2026, 1, 9, 23, 59, 59, TimeSpan.Zero));
        var later = SqliteTimestamp.ToText(new DateTimeOffset(2026, 1, 10, 0, 0, 0, TimeSpan.Zero));

        Assert.True(string.CompareOrdinal(earlier, later) < 0);
    }

    [Fact]
    public void ConnectionFactory_RejectsRelativePath()
    {
        Assert.Throws<ArgumentException>(() => new SqliteConnectionFactory("nettrace.db"));
    }
}
