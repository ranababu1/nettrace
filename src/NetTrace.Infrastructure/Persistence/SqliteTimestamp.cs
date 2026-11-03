using System.Globalization;

namespace NetTrace.Infrastructure.Persistence;

/// <summary>
/// Timestamps are stored as fixed-width ISO 8601 UTC text (for example
/// <c>2026-11-03T08:43:10.6788444Z</c>), so string order equals chronological order.
/// </summary>
public static class SqliteTimestamp
{
    private const string Format = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    public static string ToText(DateTimeOffset timestamp) =>
        timestamp.UtcDateTime.ToString(Format, CultureInfo.InvariantCulture);

    public static DateTimeOffset Parse(string text) =>
        new(DateTime.ParseExact(text, Format, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal), TimeSpan.Zero);
}
