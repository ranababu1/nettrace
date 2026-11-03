using NetTrace.Core.Time;

namespace NetTrace.Infrastructure.Tests;

internal sealed class FakeClock(DateTimeOffset utcNow) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = utcNow;

    public TimeZoneInfo LocalTimeZone { get; set; } = TimeZoneInfo.Utc;
}

/// <summary>A unique temporary directory deleted when the test finishes.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "NetTraceTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        // Pooled SQLite connections keep database files open; release them before deleting.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; temp files are left for the OS to clean up.
        }
    }
}
