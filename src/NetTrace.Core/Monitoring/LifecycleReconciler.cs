using System.Globalization;

namespace NetTrace.Core.Monitoring;

/// <summary>
/// The persisted record of an application run. <see cref="LastAliveAtUtc"/> is refreshed by a
/// periodic heartbeat; <see cref="StoppedAtUtc"/> is set only on a clean shutdown.
/// </summary>
public sealed record LifecycleSession(
    Guid SessionId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset LastAliveAtUtc,
    DateTimeOffset? StoppedAtUtc);

public sealed record LifecycleReconciliation(
    LifecycleSession NewSession,
    MonitoringGap? Gap,
    IReadOnlyList<ApplicationEvent> Events);

/// <summary>
/// Decides, at startup, what happened since the previous run. Time when NetTrace was not
/// running becomes a closed monitoring gap: <see cref="MonitoringGapReason.ApplicationNotRunning"/>
/// after a clean stop, <see cref="MonitoringGapReason.UnexpectedTermination"/> after a crash or
/// kill. In both cases network conditions are unknown, never an outage.
/// </summary>
public static class LifecycleReconciler
{
    public const string GapSource = "LifecycleReconciliation";

    public static LifecycleReconciliation Reconcile(LifecycleSession? previous, Guid newSessionId, DateTimeOffset nowUtc)
    {
        var newSession = new LifecycleSession(newSessionId, nowUtc, nowUtc, StoppedAtUtc: null);
        var events = new List<ApplicationEvent>();
        MonitoringGap? gap = null;

        if (previous is not null)
        {
            var cleanStop = previous.StoppedAtUtc is not null;
            var gapStart = previous.StoppedAtUtc ?? previous.LastAliveAtUtc;

            if (!cleanStop)
            {
                events.Add(new ApplicationEvent(nowUtc, ApplicationEventType.UncleanShutdownDetected, new Dictionary<string, string?>
                {
                    ["previous_session_id"] = previous.SessionId.ToString("D"),
                    ["last_alive_at_utc"] = FormatUtc(previous.LastAliveAtUtc),
                }));
            }

            if (gapStart > nowUtc)
            {
                // The clock moved backwards (or the previous run's clock was ahead). The interval
                // cannot be measured, so record the discontinuity instead of a negative gap.
                events.Add(new ApplicationEvent(nowUtc, ApplicationEventType.ClockDiscontinuityDetected, new Dictionary<string, string?>
                {
                    ["previous_reference_utc"] = FormatUtc(gapStart),
                    ["current_utc"] = FormatUtc(nowUtc),
                }));
            }
            else if (gapStart < nowUtc)
            {
                gap = cleanStop
                    ? new MonitoringGap(
                        gapStart,
                        nowUtc,
                        MonitoringGapReason.ApplicationNotRunning,
                        "NetTrace was not running. Network conditions during this period are unknown.",
                        GapSource)
                    : new MonitoringGap(
                        gapStart,
                        nowUtc,
                        MonitoringGapReason.UnexpectedTermination,
                        "NetTrace stopped unexpectedly; the gap starts at its last heartbeat. Network conditions during this period are unknown.",
                        GapSource);
            }
        }

        events.Add(new ApplicationEvent(nowUtc, ApplicationEventType.ApplicationStarted, new Dictionary<string, string?>
        {
            ["session_id"] = newSessionId.ToString("D"),
        }));

        return new LifecycleReconciliation(newSession, gap, events);
    }

    private static string FormatUtc(DateTimeOffset value) =>
        value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
}
