using NetTrace.Core.Incidents;
using NetTrace.Core.Monitoring;
using NetTrace.Core.Probes;
using NetTrace.Core.Statistics;

namespace NetTrace.Core.Tests;

public class DailyStatisticsTests
{
    private static readonly DateTimeOffset Day = new(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset EndOfDay = Day.AddDays(1);

    [Fact]
    public void FullyMonitoredDayWithNoIncidents_IsAllHealthy()
    {
        var s = Calc(now: EndOfDay);

        Assert.Equal(86400, s.ObservedSeconds);
        Assert.Equal(86400, s.HealthySeconds);
        Assert.Equal(100, s.UptimePercentage);
        Assert.Equal(100, s.MonitoringCoveragePercentage);
        Assert.Equal(0, s.IncidentCount);
        Assert.Null(s.LongestIncidentSeconds);
    }

    [Fact]
    public void Gaps_AreNeitherUptimeNorDowntime()
    {
        var gap = new MonitoringGap(Day.AddHours(1), Day.AddHours(3), MonitoringGapReason.ApplicationNotRunning, null, "t");

        var s = Calc(gaps: [gap], now: EndOfDay);

        Assert.Equal(7200, s.UnmonitoredSeconds);
        Assert.Equal(79200, s.ObservedSeconds);
        Assert.Equal(100, s.UptimePercentage);
        Assert.Equal(Math.Round(79200d / 86400 * 100, 6), Math.Round(s.MonitoringCoveragePercentage!.Value, 6));
    }

    [Fact]
    public void IncidentTime_CountsAsOutage_AndReducesUptime()
    {
        var s = Calc(incidents: [Incident(Day.AddHours(1), Day.AddHours(1).AddSeconds(864))], now: EndOfDay);

        Assert.Equal(864, s.OutageSeconds);
        Assert.Equal(99, s.UptimePercentage!.Value, 6);
        Assert.Equal(1, s.IncidentCount);
        Assert.Equal(864, s.LongestIncidentSeconds);
    }

    [Fact]
    public void IncidentOverlappingAGap_OnlyCountsTheMonitoredPart()
    {
        var gap = new MonitoringGap(Day.AddHours(2), Day.AddHours(3), MonitoringGapReason.SystemSleep, null, "t");

        var s = Calc(incidents: [Incident(Day.AddHours(1), Day.AddHours(4))], gaps: [gap], now: EndOfDay);

        Assert.Equal(7200, s.OutageSeconds);
        Assert.Equal(3600, s.UnmonitoredSeconds);
    }

    [Fact]
    public void OverlappingGapsAndIncidents_AreNotDoubleCounted()
    {
        var gaps = new[]
        {
            new MonitoringGap(Day.AddHours(1), Day.AddHours(2), MonitoringGapReason.Unknown, null, "t"),
            new MonitoringGap(Day.AddHours(1.5), Day.AddHours(3), MonitoringGapReason.Unknown, null, "t"),
        };

        var s = Calc(gaps: gaps, now: EndOfDay);

        Assert.Equal(7200, s.UnmonitoredSeconds);
    }

    [Fact]
    public void OpenIncidentAndOpenGap_ExtendToNow_AndFutureTimeIsExcluded()
    {
        var now = Day.AddHours(10);
        var gap = new MonitoringGap(Day.AddHours(9), null, MonitoringGapReason.MonitoringDisabled, null, "t");

        var s = Calc(incidents: [Incident(Day.AddHours(5), null)], gaps: [gap], now: now);

        Assert.Equal(36000, s.ElapsedSeconds);
        Assert.Equal(3600, s.UnmonitoredSeconds);
        Assert.Equal(4 * 3600, s.OutageSeconds);
        Assert.Equal(1, s.IncidentCount);
    }

    [Fact]
    public void IncidentSpanningMidnight_IsClippedToTheDay()
    {
        var s = Calc(incidents: [Incident(Day.AddHours(-1), Day.AddHours(1))], now: EndOfDay);

        Assert.Equal(3600, s.OutageSeconds);
        Assert.Equal(1, s.IncidentCount);
        Assert.Equal(3600, s.LongestIncidentSeconds);
    }

    [Fact]
    public void FullyUnmonitoredWindow_HasNullUptime()
    {
        var gap = new MonitoringGap(Day.AddHours(-1), null, MonitoringGapReason.ApplicationNotRunning, null, "t");

        var s = Calc(gaps: [gap], now: EndOfDay);

        Assert.Null(s.UptimePercentage);
        Assert.Equal(0, s.ObservedSeconds);
    }

    [Fact]
    public void Series_ComputeLatencyLossAndJitterPerTarget_WithoutMixingTargets()
    {
        var obs = new List<StoredObservation>();
        double[] latencies = [10, 20, 10, 30, 10, 20];
        for (var i = 0; i < latencies.Length; i++)
        {
            obs.Add(Obs(ProbeType.ExternalIcmp, "1.1.1.1", ProbeOutcome.Success, latencies[i], i));
        }

        obs.Add(Obs(ProbeType.ExternalIcmp, "1.1.1.1", ProbeOutcome.Timeout, null, 10));
        obs.Add(Obs(ProbeType.ExternalIcmp, "1.1.1.1", ProbeOutcome.Error, null, 11));
        obs.Add(Obs(ProbeType.ExternalIcmp, "8.8.8.8", ProbeOutcome.Success, 100, 0));

        var s = Calc(observations: obs, now: EndOfDay);

        var a = Assert.Single(s.Series, x => x.Target == "1.1.1.1");
        Assert.Equal(7, a.EligibleProbes);
        Assert.Equal(1, a.FailedProbes);
        Assert.Equal(100d / 7, a.PacketLossPercentage!.Value, 6);
        Assert.Equal(100d / 6, a.AverageLatencyMs!.Value, 6);
        Assert.Equal(15, a.MedianLatencyMs);
        Assert.Equal(30, a.P95LatencyMs);
        Assert.Equal(30, a.MaximumLatencyMs);
        Assert.Equal((10 + 10 + 20 + 20 + 10) / 5d, a.JitterMs!.Value, 6);

        var b = Assert.Single(s.Series, x => x.Target == "8.8.8.8");
        Assert.Equal(100, b.AverageLatencyMs);
        Assert.Null(b.MedianLatencyMs);
        Assert.Null(b.P95LatencyMs);
        Assert.Null(b.JitterMs);
    }

    [Fact]
    public void DnsAndHttpsFailures_AreCounted()
    {
        var obs = new List<StoredObservation>
        {
            Obs(ProbeType.Dns, "example.com", ProbeOutcome.Failure, null, 0),
            Obs(ProbeType.Dns, "example.com", ProbeOutcome.Success, 12, 1),
            Obs(ProbeType.Https, "https://a/", ProbeOutcome.Timeout, null, 0),
            Obs(ProbeType.Https, "https://b/", ProbeOutcome.Failure, null, 0),
        };

        var s = Calc(observations: obs, now: EndOfDay);

        Assert.Equal(1, s.DnsFailureCount);
        Assert.Equal(2, s.HttpsFailureCount);
    }

    [Fact]
    public void ObservationsOutsideTheWindow_AreIgnored()
    {
        var obs = new List<StoredObservation> { Obs(ProbeType.Gateway, "g", ProbeOutcome.Success, 1, -100000) };

        Assert.Empty(Calc(observations: obs, now: EndOfDay).Series);
    }

    private static DailyStatistics Calc(
        IEnumerable<StoredObservation>? observations = null,
        IEnumerable<Incident>? incidents = null,
        IEnumerable<MonitoringGap>? gaps = null,
        DateTimeOffset? now = null) =>
        DailyStatisticsCalculator.Calculate(Day, EndOfDay, now ?? EndOfDay, observations ?? [], incidents ?? [], gaps ?? []);

    private static Incident Incident(DateTimeOffset start, DateTimeOffset? end) =>
        new(Guid.NewGuid(), start, start.AddSeconds(15), end, IncidentClassification.GatewayUnreachable,
            ClassificationConfidence.High, "s", [], []);

    private static StoredObservation Obs(ProbeType type, string target, ProbeOutcome outcome, double? latency, int minute)
    {
        var at = Day.AddMinutes(minute);
        return new StoredObservation(
            minute, Guid.NewGuid(), 1, null,
            new ProbeResult(type, target, "m", at, at.AddMilliseconds(5), outcome, latency, 1000, null, null));
    }
}
