using System.Globalization;

namespace NetTrace.Core.Statistics;

/// <summary>Short human-readable durations and percentages for display.</summary>
public static class DurationFormatter
{
    public const string NotAvailable = "--";

    /// <summary>"45 s", "12 min 5 s", "3 h 20 min", "2 d 4 h". Negative values are treated as zero.</summary>
    public static string Format(double seconds)
    {
        var total = (long)Math.Round(Math.Max(0, seconds));
        var days = total / 86400;
        var hours = total % 86400 / 3600;
        var minutes = total % 3600 / 60;
        var secs = total % 60;
        var c = CultureInfo.InvariantCulture;

        if (days > 0)
        {
            return hours > 0 ? string.Create(c, $"{days} d {hours} h") : string.Create(c, $"{days} d");
        }

        if (hours > 0)
        {
            return minutes > 0 ? string.Create(c, $"{hours} h {minutes} min") : string.Create(c, $"{hours} h");
        }

        if (minutes > 0)
        {
            return secs > 0 ? string.Create(c, $"{minutes} min {secs} s") : string.Create(c, $"{minutes} min");
        }

        return string.Create(c, $"{secs} s");
    }

    /// <summary>
    /// Percentage with two decimals, truncated rather than rounded so a day with any outage
    /// never displays as 100.00 %. Null is shown as not available, never as zero.
    /// </summary>
    public static string FormatPercentage(double? percentage) =>
        percentage is { } p
            ? (Math.Floor(Math.Clamp(p, 0, 100) * 100) / 100).ToString("0.00", CultureInfo.CurrentCulture) + " %"
            : NotAvailable;
}
