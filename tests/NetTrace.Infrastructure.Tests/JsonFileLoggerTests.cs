using System.Text.Json;
using Microsoft.Extensions.Logging;
using NetTrace.Infrastructure.Logging;

namespace NetTrace.Infrastructure.Tests;

public class JsonFileLoggerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 23, 59, 58, TimeSpan.Zero);

    [Fact]
    public void Log_WritesStructuredJsonLine()
    {
        using var temp = new TempDirectory();
        var clock = new FakeClock(Now);
        using (var provider = new JsonFileLoggerProvider(temp.Path, clock))
        {
            var logger = provider.CreateLogger("Test.Category");
            logger.LogWarning(new EventId(7, "ProbeTimeout"), "Probe {Target} timed out after {TimeoutMs} ms", "1.1.1.1", 2000);
        }

        var line = Assert.Single(ReadLines(temp.Path, "nettrace-20261103.jsonl"));
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        Assert.Equal("2026-11-03T23:59:58.0000000Z", root.GetProperty("timestamp_utc").GetString());
        Assert.Equal("Warning", root.GetProperty("level").GetString());
        Assert.Equal("Test.Category", root.GetProperty("category").GetString());
        Assert.Equal(7, root.GetProperty("event_id").GetInt32());
        Assert.Equal("ProbeTimeout", root.GetProperty("event_name").GetString());
        Assert.Equal("Probe 1.1.1.1 timed out after 2000 ms", root.GetProperty("message").GetString());
        var properties = root.GetProperty("properties");
        Assert.Equal("1.1.1.1", properties.GetProperty("Target").GetString());
        Assert.Equal(2000, properties.GetProperty("TimeoutMs").GetInt32());
        Assert.False(properties.TryGetProperty("{OriginalFormat}", out _));
    }

    [Fact]
    public void Log_IncludesExceptionDetails()
    {
        using var temp = new TempDirectory();
        using (var provider = new JsonFileLoggerProvider(temp.Path, new FakeClock(Now)))
        {
            provider.CreateLogger("Test").LogError(new InvalidOperationException("boom"), "Failed");
        }

        using var doc = JsonDocument.Parse(Assert.Single(ReadLines(temp.Path, "nettrace-20261103.jsonl")));
        var exception = doc.RootElement.GetProperty("exception");
        Assert.Equal(typeof(InvalidOperationException).FullName, exception.GetProperty("type").GetString());
        Assert.Equal("boom", exception.GetProperty("message").GetString());
    }

    [Fact]
    public void Log_BelowMinimumLevel_IsNotWritten()
    {
        using var temp = new TempDirectory();
        using (var provider = new JsonFileLoggerProvider(temp.Path, new FakeClock(Now), LogLevel.Warning))
        {
            var logger = provider.CreateLogger("Test");
            Assert.False(logger.IsEnabled(LogLevel.Information));
            logger.LogInformation("ignored");
            logger.LogWarning("kept");
        }

        var line = Assert.Single(ReadLines(temp.Path, "nettrace-20261103.jsonl"));
        Assert.Contains("kept", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Log_RollsOverToNewFileOnUtcDateChange()
    {
        using var temp = new TempDirectory();
        var clock = new FakeClock(Now);
        using (var provider = new JsonFileLoggerProvider(temp.Path, clock))
        {
            var logger = provider.CreateLogger("Test");
            logger.LogInformation("before midnight");
            clock.UtcNow = Now.AddSeconds(5);
            logger.LogInformation("after midnight");
        }

        Assert.Contains("before midnight", Assert.Single(ReadLines(temp.Path, "nettrace-20261103.jsonl")), StringComparison.Ordinal);
        Assert.Contains("after midnight", Assert.Single(ReadLines(temp.Path, "nettrace-20261104.jsonl")), StringComparison.Ordinal);
    }

    [Fact]
    public void Log_AfterDispose_IsIgnored()
    {
        using var temp = new TempDirectory();
        var provider = new JsonFileLoggerProvider(temp.Path, new FakeClock(Now));
        var logger = provider.CreateLogger("Test");
        provider.Dispose();

        logger.LogError("late");

        Assert.False(File.Exists(Path.Combine(temp.Path, "nettrace-20261103.jsonl")));
    }

    [Fact]
    public void GetFileName_UsesUtcDate()
    {
        var timestamp = new DateTimeOffset(2026, 11, 4, 1, 0, 0, TimeSpan.FromHours(5.5));

        Assert.Equal("nettrace-20261103.jsonl", JsonFileLoggerProvider.GetFileName(timestamp));
    }

    private static string[] ReadLines(string directory, string fileName) =>
        File.ReadAllLines(Path.Combine(directory, fileName)).Where(l => l.Length > 0).ToArray();
}
