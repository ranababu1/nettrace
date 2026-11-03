namespace NetTrace.Core.Probes;

/// <summary>What was measured. Serialized by name; never renumber or rename.</summary>
public enum ProbeType
{
    Gateway,
    ExternalIcmp,
    Dns,
    Https,
}

/// <summary>
/// How a probe ended. <see cref="Skipped"/> means the probe could not be attempted (for
/// example no default gateway exists); it is distinct from a failed attempt.
/// Serialized by name; never renumber or rename.
/// </summary>
public enum ProbeOutcome
{
    Success,
    Failure,
    Timeout,
    Error,
    Skipped,
}

/// <summary>
/// One probe attempt. <see cref="LatencyMs"/> is null whenever no latency was measured, never
/// zero. <see cref="ErrorMessage"/> is fixed descriptive text chosen from the error code, so it
/// never contains raw exception text.
/// </summary>
public sealed record ProbeResult(
    ProbeType ProbeType,
    string Target,
    string Method,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    ProbeOutcome Outcome,
    double? LatencyMs,
    int TimeoutMilliseconds,
    string? ErrorCode,
    string? ErrorMessage)
{
    public bool Succeeded => Outcome == ProbeOutcome.Success;

    public bool TimedOut => Outcome == ProbeOutcome.Timeout;

    public double DurationMs => (CompletedAtUtc - StartedAtUtc).TotalMilliseconds;

    public static ProbeResult Skip(
        ProbeType probeType, string target, string method, DateTimeOffset atUtc, int timeoutMilliseconds, string errorCode, string errorMessage) =>
        new(probeType, target, method, atUtc, atUtc, ProbeOutcome.Skipped, null, timeoutMilliseconds, errorCode, errorMessage);
}

public interface IIcmpProbe
{
    /// <summary>
    /// Sends one ICMP echo to <paramref name="target"/> (an IP literal). Never throws for network
    /// failures; they are reported in the result. Throws only for cancellation.
    /// </summary>
    Task<ProbeResult> PingAsync(ProbeType probeType, string target, int timeoutMilliseconds, CancellationToken cancellationToken);
}
