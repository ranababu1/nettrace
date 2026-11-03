using NetTrace.Core.Time;

namespace NetTrace.Infrastructure.Time;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public TimeZoneInfo LocalTimeZone => TimeZoneInfo.Local;
}
