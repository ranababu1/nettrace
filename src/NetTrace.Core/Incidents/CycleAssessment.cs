using NetTrace.Core.Monitoring;
using NetTrace.Core.Probes;

namespace NetTrace.Core.Incidents;

/// <summary>What one cycle's evidence says. Serialized by name; never renumber or rename.</summary>
public enum CycleHealth
{
    Healthy,
    Failing,

    /// <summary>The evidence is too thin or contradictory to call the cycle healthy or failing.</summary>
    Inconclusive,
}

/// <summary>
/// Suggested incident classifications. Wording is deliberately non-committal about root cause:
/// there is no "ISP outage" value. Serialized by name; never renumber or rename.
/// </summary>
public enum IncidentClassification
{
    LocalInterfaceDisconnected,
    GatewayUnreachable,
    ExternalConnectivityUnavailable,
    DnsResolutionFailure,
    HttpsConnectivityFailure,
}

public enum ClassificationConfidence
{
    Low,
    Medium,
    High,
}

/// <summary>
/// DNS and HTTPS probes run on a slower cadence than the cycle, so most cycles carry no fresh
/// result for them. This holds the latest known state so a streak of failing cycles is not
/// broken by cycles that merely lack DNS/HTTPS evidence.
/// </summary>
public sealed record SlowProbeEvidence(bool? DnsFailed, int HttpsMeasured, int HttpsFailed)
{
    public static SlowProbeEvidence None { get; } = new(null, 0, 0);

    public bool HttpsAnySucceeded => HttpsMeasured > HttpsFailed;

    public bool HttpsAllFailed => HttpsMeasured > 0 && HttpsFailed == HttpsMeasured;

    /// <summary>Takes fresh DNS/HTTPS results from <paramref name="cycle"/> where it has any; otherwise keeps this state.</summary>
    public SlowProbeEvidence Update(MonitoringCycle cycle)
    {
        var dns = cycle.Results.Where(CycleClassifier.IsMeasured).Where(r => r.ProbeType == ProbeType.Dns).ToList();
        var https = cycle.Results.Where(CycleClassifier.IsMeasured).Where(r => r.ProbeType == ProbeType.Https).ToList();
        return new SlowProbeEvidence(
            dns.Count > 0 ? dns.All(CycleClassifier.IsFailed) : DnsFailed,
            https.Count > 0 ? https.Count : HttpsMeasured,
            https.Count > 0 ? https.Count(CycleClassifier.IsFailed) : HttpsFailed);
    }
}

public sealed record CycleAssessment(
    CycleHealth Health,
    IncidentClassification? Classification,
    ClassificationConfidence Confidence,
    string Summary,
    IReadOnlyList<string> FailedProbes,
    IReadOnlyList<string> WorkingProbes);
