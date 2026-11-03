using NetTrace.Core.Monitoring;
using NetTrace.Core.Probes;

namespace NetTrace.Core.Incidents;

/// <summary>
/// Turns one cycle's probe evidence into a health verdict following plan.md section 4.3. A cycle
/// only counts as failing when independent probes agree; ICMP-only or single-destination
/// failures are never failing on their own. Skipped and errored probes are not evidence.
/// </summary>
public static class CycleClassifier
{
    internal static bool IsMeasured(ProbeResult r) => r.Outcome is ProbeOutcome.Success or ProbeOutcome.Failure or ProbeOutcome.Timeout;

    internal static bool IsFailed(ProbeResult r) => r.Outcome is ProbeOutcome.Failure or ProbeOutcome.Timeout;

    public static CycleAssessment Assess(MonitoringCycle cycle, SlowProbeEvidence slow)
    {
        ArgumentNullException.ThrowIfNull(cycle);
        ArgumentNullException.ThrowIfNull(slow);

        var measured = cycle.Results.Where(IsMeasured).ToList();
        var failed = measured.Where(IsFailed).Select(Describe).ToList();
        var working = measured.Where(r => r.Succeeded).Select(Describe).ToList();

        var gateway = cycle.Results.FirstOrDefault(r => r.ProbeType == ProbeType.Gateway);
        var externals = measured.Where(r => r.ProbeType == ProbeType.ExternalIcmp).ToList();
        var externalsFailed = externals.Count(IsFailed);
        var allExternalsFailed = externals.Count > 0 && externalsFailed == externals.Count;
        var anyExternalOk = externals.Count > externalsFailed;
        var gatewayFailed = gateway is not null && IsFailed(gateway);

        CycleAssessment Make(CycleHealth health, IncidentClassification? c, ClassificationConfidence conf, string text) =>
            new(health, c, conf, text, failed, working);

        // Any success on an independent path means there is no confirmed loss of connectivity.
        if (slow.HttpsAnySucceeded && (allExternalsFailed || gatewayFailed))
        {
            return Make(CycleHealth.Healthy, null, ClassificationConfidence.Low,
                "ICMP probes failed but HTTPS succeeded: an ICMP-specific or target-specific failure, not a confirmed outage.");
        }

        if (!cycle.NetworkContext.HasActiveConnection && gateway is { Outcome: ProbeOutcome.Skipped })
        {
            return anyExternalOk
                ? Make(CycleHealth.Healthy, null, ClassificationConfidence.Low, "External targets responded.")
                : Make(CycleHealth.Failing, IncidentClassification.LocalInterfaceDisconnected, ClassificationConfidence.High,
                    "No connected network adapter with a default gateway was found.");
        }

        if (gatewayFailed)
        {
            if (allExternalsFailed)
            {
                return Make(CycleHealth.Failing, IncidentClassification.GatewayUnreachable,
                    externals.Count >= 2 ? ClassificationConfidence.High : ClassificationConfidence.Medium,
                    "The default gateway and every external target did not respond.");
            }

            return anyExternalOk
                ? Make(CycleHealth.Healthy, null, ClassificationConfidence.Low, "The gateway did not answer ICMP but external targets did.")
                : Make(CycleHealth.Inconclusive, null, ClassificationConfidence.Low, "Only the gateway was measured and it did not respond.");
        }

        if (allExternalsFailed)
        {
            // Corroboration: several independent targets, or HTTPS agreeing with ICMP.
            if (externals.Count >= 2 || slow.HttpsAllFailed)
            {
                return Make(CycleHealth.Failing, IncidentClassification.ExternalConnectivityUnavailable,
                    slow.HttpsAllFailed && externals.Count >= 2 ? ClassificationConfidence.High : ClassificationConfidence.Medium,
                    "The gateway responded but external targets did not.");
            }

            return Make(CycleHealth.Inconclusive, null, ClassificationConfidence.Low, "A single external target failed; this is not corroborated.");
        }

        if (slow.DnsFailed == true && (anyExternalOk || slow.HttpsAnySucceeded))
        {
            return Make(CycleHealth.Failing, IncidentClassification.DnsResolutionFailure, ClassificationConfidence.Medium,
                "IP connectivity works but the DNS test name did not resolve.");
        }

        if (slow.HttpsAllFailed && slow.HttpsMeasured >= 2 && anyExternalOk)
        {
            return Make(CycleHealth.Failing, IncidentClassification.HttpsConnectivityFailure, ClassificationConfidence.Medium,
                "External targets responded but every HTTPS endpoint failed.");
        }

        return measured.Count == 0
            ? Make(CycleHealth.Inconclusive, null, ClassificationConfidence.Low, "No probe produced a measurement.")
            : Make(CycleHealth.Healthy, null, ClassificationConfidence.Medium, "Probes responded normally.");
    }

    private static string Describe(ProbeResult r) => $"{r.ProbeType} {r.Target}";
}
