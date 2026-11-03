using NetTrace.Core.Incidents;
using NetTrace.Core.Monitoring;
using NetTrace.Core.Probes;

namespace NetTrace.Core.Statistics;

/// <summary>
/// Latency and loss for one probe type and target. Measurements from different targets or
/// protocols are never merged. Values that cannot be computed are null, never zero.
/// </summary>
public sealed record ProbeSeriesStatistics(
    ProbeType ProbeType,
    string Target,
    int EligibleProbes,
    int FailedProbes,
    double? PacketLossPercentage,
    double? AverageLatencyMs,
    double? MedianLatencyMs,
    double? P95LatencyMs,
    double? MaximumLatencyMs,
    double? JitterMs);

/// <summary>
/// One day (or any window) of statistics. Uptime = <see cref="HealthySeconds"/> divided by
/// <see cref="ObservedSeconds"/>; unmonitored time counts as neither uptime nor downtime.
/// </summary>
public sealed record DailyStatistics(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    double ElapsedSeconds,
    double ObservedSeconds,
    double HealthySeconds,
    double OutageSeconds,
    double UnmonitoredSeconds,
    double? UptimePercentage,
    double? MonitoringCoveragePercentage,
    int IncidentCount,
    double? LongestIncidentSeconds,
    int DnsFailureCount,
    int HttpsFailureCount,
    IReadOnlyList<ProbeSeriesStatistics> Series);

public static class DailyStatisticsCalculator
{
    /// <summary>Percentiles and the median need at least this many successful samples.</summary>
    public const int MinimumSamplesForPercentiles = 5;

    /// <param name="nowUtc">Time that has not happened yet is excluded: the window ends at the earlier of <paramref name="toUtc"/> and this.</param>
    public static DailyStatistics Calculate(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        DateTimeOffset nowUtc,
        IEnumerable<StoredObservation> observations,
        IEnumerable<Incident> incidents,
        IEnumerable<MonitoringGap> gaps)
    {
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(incidents);
        ArgumentNullException.ThrowIfNull(gaps);
        if (toUtc <= fromUtc)
        {
            throw new ArgumentException("The window must end after it starts.", nameof(toUtc));
        }

        var end = toUtc < nowUtc ? toUtc : nowUtc;
        if (end < fromUtc)
        {
            end = fromUtc;
        }

        var elapsed = (end - fromUtc).TotalSeconds;
        var gapIntervals = Merge(gaps.Select(g => Clip(g.StartedAtUtc, g.EndedAtUtc ?? end, fromUtc, end)));
        var incidentIntervals = Merge(incidents.Select(i => Clip(i.StartedAtUtc, i.EndedAtUtc ?? end, fromUtc, end)));
        var incidentCount = incidents.Count(i => i.StartedAtUtc < end && (i.EndedAtUtc ?? end) > fromUtc);
        var incidentLengths = incidents
            .Where(i => i.StartedAtUtc < end && (i.EndedAtUtc ?? end) > fromUtc)
            .Select(i => Clip(i.StartedAtUtc, i.EndedAtUtc ?? end, fromUtc, end))
            .Select(Length)
            .ToList();

        var unmonitored = Total(gapIntervals);
        var observed = elapsed - unmonitored;

        // Outage time that overlaps a gap is unknown, not outage.
        var outage = Total(incidentIntervals) - Total(Intersect(incidentIntervals, gapIntervals));
        var healthy = Math.Max(0, observed - outage);

        var inWindow = observations
            .Where(o => o.Result.StartedAtUtc >= fromUtc && o.Result.StartedAtUtc < end)
            .ToList();

        return new DailyStatistics(
            fromUtc,
            end,
            elapsed,
            observed,
            healthy,
            outage,
            unmonitored,
            observed > 0 ? healthy / observed * 100 : null,
            elapsed > 0 ? observed / elapsed * 100 : null,
            incidentCount,
            incidentLengths.Count > 0 ? incidentLengths.Max() : null,
            inWindow.Count(o => o.Result.ProbeType == ProbeType.Dns && IsFailed(o.Result)),
            inWindow.Count(o => o.Result.ProbeType == ProbeType.Https && IsFailed(o.Result)),
            BuildSeries(inWindow));
    }

    private static List<ProbeSeriesStatistics> BuildSeries(List<StoredObservation> observations) =>
        observations
            .Select(o => o.Result)
            .Where(r => IsFailed(r) || r.Succeeded)
            .GroupBy(r => (r.ProbeType, r.Target))
            .OrderBy(g => g.Key.ProbeType)
            .ThenBy(g => g.Key.Target, StringComparer.Ordinal)
            .Select(g =>
            {
                var results = g.OrderBy(r => r.StartedAtUtc).ToList();
                var latencies = results.Where(r => r.Succeeded && r.LatencyMs is not null).Select(r => r.LatencyMs!.Value).ToList();
                var failed = results.Count(IsFailed);
                var sorted = latencies.Order().ToList();
                return new ProbeSeriesStatistics(
                    g.Key.ProbeType,
                    g.Key.Target,
                    results.Count,
                    failed,
                    (double)failed / results.Count * 100,
                    latencies.Count > 0 ? latencies.Average() : null,
                    sorted.Count >= MinimumSamplesForPercentiles ? Median(sorted) : null,
                    sorted.Count >= MinimumSamplesForPercentiles ? NearestRank(sorted, 0.95) : null,
                    sorted.Count > 0 ? sorted[^1] : null,
                    Jitter(latencies));
            })
            .ToList();

    private static bool IsFailed(ProbeResult r) => r.Outcome is ProbeOutcome.Failure or ProbeOutcome.Timeout;

    private static double Median(List<double> sorted) =>
        sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[(sorted.Count / 2) - 1] + sorted[sorted.Count / 2]) / 2;

    private static double NearestRank(List<double> sorted, double percentile) =>
        sorted[Math.Max(0, (int)Math.Ceiling(percentile * sorted.Count) - 1)];

    /// <summary>Mean absolute difference between consecutive successful samples (plan.md 4.4).</summary>
    private static double? Jitter(List<double> latenciesInTimeOrder)
    {
        if (latenciesInTimeOrder.Count < 2)
        {
            return null;
        }

        var total = 0.0;
        for (var i = 1; i < latenciesInTimeOrder.Count; i++)
        {
            total += Math.Abs(latenciesInTimeOrder[i] - latenciesInTimeOrder[i - 1]);
        }

        return total / (latenciesInTimeOrder.Count - 1);
    }

    private static (DateTimeOffset Start, DateTimeOffset End) Clip(
        DateTimeOffset start, DateTimeOffset end, DateTimeOffset from, DateTimeOffset to)
    {
        var s = start < from ? from : start;
        var e = end > to ? to : end;
        return e > s ? (s, e) : (s, s);
    }

    private static double Length((DateTimeOffset Start, DateTimeOffset End) i) => (i.End - i.Start).TotalSeconds;

    private static double Total(List<(DateTimeOffset Start, DateTimeOffset End)> intervals) => intervals.Sum(Length);

    private static List<(DateTimeOffset Start, DateTimeOffset End)> Merge(IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> intervals)
    {
        var merged = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        foreach (var i in intervals.Where(i => i.End > i.Start).OrderBy(i => i.Start))
        {
            if (merged.Count > 0 && i.Start <= merged[^1].End)
            {
                merged[^1] = (merged[^1].Start, i.End > merged[^1].End ? i.End : merged[^1].End);
            }
            else
            {
                merged.Add(i);
            }
        }

        return merged;
    }

    /// <summary>Both inputs must already be merged (sorted, non-overlapping).</summary>
    private static List<(DateTimeOffset Start, DateTimeOffset End)> Intersect(
        List<(DateTimeOffset Start, DateTimeOffset End)> a, List<(DateTimeOffset Start, DateTimeOffset End)> b)
    {
        var result = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        int x = 0, y = 0;
        while (x < a.Count && y < b.Count)
        {
            var start = a[x].Start > b[y].Start ? a[x].Start : b[y].Start;
            var end = a[x].End < b[y].End ? a[x].End : b[y].End;
            if (end > start)
            {
                result.Add((start, end));
            }

            if (a[x].End < b[y].End)
            {
                x++;
            }
            else
            {
                y++;
            }
        }

        return result;
    }
}
