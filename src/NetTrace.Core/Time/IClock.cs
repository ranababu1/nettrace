namespace NetTrace.Core.Time;

/// <summary>
/// Source of the current time. UTC is authoritative for stored timestamps; the local
/// time zone is used only for display and day-wise reporting.
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }

    TimeZoneInfo LocalTimeZone { get; }
}
