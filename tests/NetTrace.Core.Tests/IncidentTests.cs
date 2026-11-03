using NetTrace.Core.Configuration;
using NetTrace.Core.Incidents;
using NetTrace.Core.Monitoring;
using NetTrace.Core.Network;
using NetTrace.Core.Probes;

namespace NetTrace.Core.Tests;

public class IncidentTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    // ---- classification ----

    [Fact]
    public void Healthy_WhenEverythingResponds()
    {
        var a = Assess(Cycle(0));

        Assert.Equal(CycleHealth.Healthy, a.Health);
    }

    [Fact]
    public void GatewayAndAllExternalsFail_IsGatewayUnreachable()
    {
        var a = Assess(Cycle(0, gateway: false, e1: false, e2: false));

        Assert.Equal(CycleHealth.Failing, a.Health);
        Assert.Equal(IncidentClassification.GatewayUnreachable, a.Classification);
        Assert.Equal(ClassificationConfidence.High, a.Confidence);
        Assert.Equal(3, a.FailedProbes.Count);
    }

    [Fact]
    public void GatewayOkExternalsFail_IsExternalConnectivityUnavailable()
    {
        var a = Assess(Cycle(0, e1: false, e2: false));

        Assert.Equal(IncidentClassification.ExternalConnectivityUnavailable, a.Classification);
        Assert.Contains("Gateway 192.168.1.1", a.WorkingProbes);
    }

    [Fact]
    public void OneExternalFailing_IsNotAFailure()
    {
        Assert.Equal(CycleHealth.Healthy, Assess(Cycle(0, e1: false)).Health);
    }

    [Fact]
    public void GatewayIgnoresIcmpButExternalsRespond_IsHealthy()
    {
        Assert.Equal(CycleHealth.Healthy, Assess(Cycle(0, gateway: false)).Health);
    }

    [Fact]
    public void NoActiveConnection_IsLocalInterfaceDisconnected()
    {
        var a = Assess(Cycle(0, noConnection: true, e1: false, e2: false));

        Assert.Equal(IncidentClassification.LocalInterfaceDisconnected, a.Classification);
    }

    [Fact]
    public void IcmpFailsButHttpsSucceeds_IsNotAConfirmedOutage()
    {
        var slow = new SlowProbeEvidence(false, 2, 0);

        var a = CycleClassifier.Assess(Cycle(0, e1: false, e2: false), slow);

        Assert.Equal(CycleHealth.Healthy, a.Health);
        Assert.Contains("ICMP-specific", a.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ExternalsAndHttpsFail_RaisesConfidenceToHigh()
    {
        var a = CycleClassifier.Assess(Cycle(0, e1: false, e2: false), new SlowProbeEvidence(null, 2, 2));

        Assert.Equal(ClassificationConfidence.High, a.Confidence);
    }

    [Fact]
    public void DnsFailureWithWorkingIp_IsDnsResolutionFailure()
    {
        var a = CycleClassifier.Assess(Cycle(0), new SlowProbeEvidence(true, 2, 0));

        Assert.Equal(IncidentClassification.DnsResolutionFailure, a.Classification);
    }

    [Fact]
    public void AllHttpsFailWithWorkingIp_IsHttpsFailure_ButSingleEndpointIsNot()
    {
        var all = CycleClassifier.Assess(Cycle(0), new SlowProbeEvidence(false, 2, 2));
        var single = CycleClassifier.Assess(Cycle(0), new SlowProbeEvidence(false, 1, 1));

        Assert.Equal(IncidentClassification.HttpsConnectivityFailure, all.Classification);
        Assert.Equal(CycleHealth.Healthy, single.Health);
    }

    [Fact]
    public void ErroredAndSkippedProbes_AreNotEvidence()
    {
        var cycle = CycleO(0, e1: ProbeOutcome.Error, e2: ProbeOutcome.Error);

        Assert.Equal(CycleHealth.Healthy, Assess(cycle).Health);
    }

    // ---- tracking ----

    [Fact]
    public void SingleFailedCycle_IsSuspectedOnly_AndClearsWhenHealthy()
    {
        var tracker = new IncidentTracker(new MonitoringConfiguration());

        var first = tracker.Observe(Down(0));
        var second = tracker.Observe(Up(1));

        Assert.Equal(IncidentChangeKind.SuspectedStarted, Assert.Single(first).Kind);
        Assert.Equal(IncidentChangeKind.SuspectedCleared, Assert.Single(second).Kind);
        Assert.Null(tracker.Open);
    }

    [Fact]
    public void ThreeConsecutiveFailures_ConfirmWithStartAndDetectionTimes()
    {
        var tracker = new IncidentTracker(new MonitoringConfiguration());

        tracker.Observe(Down(0));
        tracker.Observe(Down(1));
        Assert.True(tracker.IsSuspected);
        var changes = tracker.Observe(Down(2));

        var confirmed = Assert.Single(changes);
        Assert.Equal(IncidentChangeKind.Confirmed, confirmed.Kind);
        Assert.Equal(T(0), confirmed.Incident!.StartedAtUtc);
        Assert.Equal(T(2).AddSeconds(0.1), confirmed.Incident.DetectedAtUtc);
        Assert.Null(confirmed.Incident.EndedAtUtc);
        Assert.Equal(IncidentClassification.ExternalConnectivityUnavailable, confirmed.Incident.Classification);
        Assert.False(tracker.IsSuspected);
    }

    [Fact]
    public void InterleavedHealthyCycle_ResetsTheFailureStreak()
    {
        var tracker = new IncidentTracker(new MonitoringConfiguration());

        tracker.Observe(Down(0));
        tracker.Observe(Down(1));
        tracker.Observe(Up(2));
        tracker.Observe(Down(3));
        tracker.Observe(Down(4));

        Assert.Null(tracker.Open);
    }

    [Fact]
    public void InconclusiveCycle_NeitherAdvancesNorResetsTheStreak()
    {
        var tracker = new IncidentTracker(new MonitoringConfiguration());

        tracker.Observe(Down(0));
        tracker.Observe(CycleO(1, e1: ProbeOutcome.Error, e2: ProbeOutcome.Error, gateway: ProbeOutcome.Error));
        tracker.Observe(Down(2));
        tracker.Observe(Down(3));

        Assert.NotNull(tracker.Open);
    }

    [Fact]
    public void Incident_ClosesOnlyAfterRecoveryThreshold_AndEndsAtFirstHealthyCycle()
    {
        var tracker = ConfirmedTracker();

        Assert.Empty(tracker.Observe(Up(3)));
        Assert.Empty(tracker.Observe(Up(4)));
        var changes = tracker.Observe(Up(5));

        var closed = Assert.Single(changes);
        Assert.Equal(IncidentChangeKind.Closed, closed.Kind);
        Assert.Equal(T(3), closed.Incident!.EndedAtUtc);
        Assert.Null(tracker.Open);
    }

    [Fact]
    public void FailureDuringRecovery_KeepsIncidentOpen()
    {
        var tracker = ConfirmedTracker();

        tracker.Observe(Up(3));
        tracker.Observe(Up(4));
        tracker.Observe(Down(5));
        tracker.Observe(Up(6));
        tracker.Observe(Up(7));

        Assert.NotNull(tracker.Open);
    }

    [Fact]
    public void FailureWithinMergeWindow_ReopensTheSameIncident()
    {
        var tracker = ConfirmedTracker();
        var original = tracker.Open!;
        tracker.Observe(Up(3));
        tracker.Observe(Up(4));
        tracker.Observe(Up(5));

        // The incident ended at T(3); the new streak starts 20 s later, inside the 60 s window.
        tracker.Observe(Down(7));
        tracker.Observe(Down(8));
        var changes = tracker.Observe(Down(9));

        var reopened = Assert.Single(changes);
        Assert.Equal(IncidentChangeKind.Reopened, reopened.Kind);
        Assert.Equal(original.Id, reopened.Incident!.Id);
        Assert.Equal(original.StartedAtUtc, reopened.Incident.StartedAtUtc);
        Assert.Null(reopened.Incident.EndedAtUtc);
    }

    [Fact]
    public void FailureAfterMergeWindow_StartsANewIncident()
    {
        var tracker = ConfirmedTracker();
        var original = tracker.Open!;
        tracker.Observe(Up(3));
        tracker.Observe(Up(4));
        tracker.Observe(Up(5));

        tracker.Observe(Down(30));
        tracker.Observe(Down(31));
        var changes = tracker.Observe(Down(32));

        var confirmed = Assert.Single(changes);
        Assert.Equal(IncidentChangeKind.Confirmed, confirmed.Kind);
        Assert.NotEqual(original.Id, confirmed.Incident!.Id);
    }

    [Fact]
    public void Gap_ClosesOpenIncidentAtLastFailure_AndDropsSuspicion()
    {
        var open = ConfirmedTracker();
        var closed = Assert.Single(open.ObserveGap(T(10)));
        Assert.Equal(IncidentChangeKind.Closed, closed.Kind);
        Assert.Equal(T(2).AddSeconds(0.1), closed.Incident!.EndedAtUtc);

        var suspected = new IncidentTracker(new MonitoringConfiguration());
        suspected.Observe(Down(0));
        Assert.Equal(IncidentChangeKind.SuspectedCleared, Assert.Single(suspected.ObserveGap(T(10))).Kind);
        Assert.False(suspected.IsSuspected);
    }

    [Fact]
    public void GapPreventsMergingWithAnEarlierIncident()
    {
        var tracker = ConfirmedTracker();
        tracker.ObserveGap(T(10));

        tracker.Observe(Down(11));
        tracker.Observe(Down(12));
        var changes = tracker.Observe(Down(13));

        Assert.Equal(IncidentChangeKind.Confirmed, Assert.Single(changes).Kind);
    }

    [Fact]
    public void DnsFailure_StaysFailingOnCyclesWithoutFreshDnsResults()
    {
        var tracker = new IncidentTracker(new MonitoringConfiguration());

        tracker.Observe(CycleO(0, dns: ProbeOutcome.Failure));
        tracker.Observe(Cycle(1));
        var changes = tracker.Observe(Cycle(2));

        var confirmed = Assert.Single(changes);
        Assert.Equal(IncidentClassification.DnsResolutionFailure, confirmed.Incident!.Classification);
    }

    // ---- helpers ----

    private static CycleAssessment Assess(MonitoringCycle cycle) => CycleClassifier.Assess(cycle, SlowProbeEvidence.None);

    private static DateTimeOffset T(int cycleIndex) => T0.AddSeconds(cycleIndex * 5);

    private static IncidentTracker ConfirmedTracker()
    {
        var tracker = new IncidentTracker(new MonitoringConfiguration());
        tracker.Observe(Down(0));
        tracker.Observe(Down(1));
        tracker.Observe(Down(2));
        Assert.NotNull(tracker.Open);
        return tracker;
    }

    private static MonitoringCycle Down(int index) => Cycle(index, e1: false, e2: false);

    private static MonitoringCycle Up(int index) => Cycle(index);

    private static MonitoringCycle Cycle(int index, bool gateway = true, bool e1 = true, bool e2 = true, bool noConnection = false) =>
        CycleO(
            index,
            gateway: noConnection ? ProbeOutcome.Skipped : gateway ? ProbeOutcome.Success : ProbeOutcome.Timeout,
            e1: e1 ? ProbeOutcome.Success : ProbeOutcome.Timeout,
            e2: e2 ? ProbeOutcome.Success : ProbeOutcome.Timeout,
            noConnection: noConnection);

    private static MonitoringCycle CycleO(
        int index,
        ProbeOutcome gateway = ProbeOutcome.Success,
        ProbeOutcome e1 = ProbeOutcome.Success,
        ProbeOutcome e2 = ProbeOutcome.Success,
        ProbeOutcome? dns = null,
        bool noConnection = false)
    {
        var start = T(index);
        var results = new List<ProbeResult>
        {
            Probe(ProbeType.Gateway, "192.168.1.1", gateway, start),
            Probe(ProbeType.ExternalIcmp, "1.1.1.1", e1, start),
            Probe(ProbeType.ExternalIcmp, "8.8.8.8", e2, start),
        };
        if (dns is { } d)
        {
            results.Add(Probe(ProbeType.Dns, "example.com", d, start));
        }

        NetworkInterfaceSnapshot? nic = noConnection
            ? null
            : new("id", "Wi-Fi", "Test", InterfaceKind.Wifi, InterfaceStatus.Up, 12, ["192.168.1.20"], [], ["192.168.1.1"], []);
        var context = new NetworkContext(start, nic is null ? [] : [nic], nic, nic is null ? null : "192.168.1.1");
        return new MonitoringCycle(Guid.NewGuid(), start, start, start.AddSeconds(0.1), 1, context, results);
    }

    private static ProbeResult Probe(ProbeType type, string target, ProbeOutcome outcome, DateTimeOffset start) =>
        new(type, target, "fake", start, start.AddMilliseconds(10), outcome,
            outcome == ProbeOutcome.Success ? 10 : null, 1000, null, null);
}
