namespace NetTrace.Core.Incidents;

/// <summary>
/// A confirmed period of failing connectivity. <see cref="StartedAtUtc"/> is the first failing
/// cycle, <see cref="DetectedAtUtc"/> when the threshold confirmed it, and a null
/// <see cref="EndedAtUtc"/> means it is ongoing. The classification states what the evidence
/// supports, not a proven root cause.
/// </summary>
public sealed record Incident(
    Guid Id,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset DetectedAtUtc,
    DateTimeOffset? EndedAtUtc,
    IncidentClassification Classification,
    ClassificationConfidence Confidence,
    string Summary,
    IReadOnlyList<string> FailedProbes,
    IReadOnlyList<string> WorkingProbes);

/// <summary>Serialized by name; never renumber or rename.</summary>
public enum IncidentChangeKind
{
    /// <summary>First failing cycle of a possible incident; not yet confirmed.</summary>
    SuspectedStarted,

    /// <summary>A suspected incident ended before reaching the confirmation threshold.</summary>
    SuspectedCleared,

    Confirmed,

    /// <summary>Failures resumed within the merge window; the earlier incident is open again.</summary>
    Reopened,

    Closed,

    /// <summary>Monitoring was interrupted (sleep, shutdown, disabled) while the incident was open. This is not a recovery.</summary>
    Interrupted,
}

public sealed record IncidentChange(IncidentChangeKind Kind, DateTimeOffset AtUtc, Incident? Incident, string? Summary);
