using Microsoft.Data.Sqlite;

namespace NetTrace.Infrastructure.Persistence;

/// <summary>
/// Bounded retry for SQLite busy/locked errors that outlast the connection busy timeout.
/// Any other error, or exhausting the attempts, is rethrown to the caller.
/// </summary>
public static class SqliteRetry
{
    public const int DefaultMaxAttempts = 3;

    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;

    private static readonly TimeSpan DefaultBaseDelay = TimeSpan.FromMilliseconds(200);

    public static async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default,
        int maxAttempts = DefaultMaxAttempts,
        TimeSpan? baseDelay = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        var delay = baseDelay ?? DefaultBaseDelay;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation(cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException ex) when (IsTransient(ex) && attempt < maxAttempts)
            {
                await Task.Delay(delay * attempt, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public static bool IsTransient(SqliteException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.SqliteErrorCode is SqliteBusy or SqliteLocked;
    }
}
