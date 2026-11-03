namespace NetTrace.Core.Monitoring;

/// <summary>Why no measurements exist for a period. Serialized by name; never renumber or rename.</summary>
public enum MonitoringGapReason
{
    Unknown,
    ApplicationNotRunning,
    UnexpectedTermination,
    SystemSleep,
    MonitoringDisabled,
}

/// <summary>
/// A period without measurements. Gaps are excluded from uptime calculations and are never
/// reported as outages: network conditions during a gap are unknown.
/// </summary>
public sealed record MonitoringGap(
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? EndedAtUtc,
    MonitoringGapReason Reason,
    string? Details,
    string Source);

/// <summary>Application lifecycle markers. Serialized by name; never renumber or rename.</summary>
public enum ApplicationEventType
{
    ApplicationStarted,
    ApplicationStopped,
    UncleanShutdownDetected,
    ClockDiscontinuityDetected,
}

public sealed record ApplicationEvent(
    DateTimeOffset OccurredAtUtc,
    ApplicationEventType EventType,
    IReadOnlyDictionary<string, string?>? Details = null);
