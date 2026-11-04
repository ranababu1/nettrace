using NetTrace.Core.Configuration;
using NetTrace.Core.Incidents;
using NetTrace.Core.Monitoring;
using NetTrace.Core.Statistics;

namespace NetTrace.Core.Notifications;

public enum NotificationKind
{
    Outage,
    Recovery,
    MonitoringProblem,
}

public sealed record Notification(NotificationKind Kind, string Title, string Message);

/// <summary>
/// Decides which events deserve a notification. Prevents storms: an incident is announced once,
/// "restored" is only sent for an incident that was announced, an interruption (sleep, shutdown)
/// is never reported as a recovery, a quickly reopened incident does not re-alert within the
/// cooldown, and a repeating monitoring error is not repeated within its cooldown.
/// Not thread-safe; callers serialise access.
/// </summary>
public sealed class NotificationPolicy
{
    public static readonly TimeSpan ReopenCooldown = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MonitoringProblemCooldown = TimeSpan.FromMinutes(30);

    private readonly HashSet<Guid> _announced = [];
    private DateTimeOffset? _lastOutageNotifiedAt;
    private string? _lastProblemKey;
    private DateTimeOffset _lastProblemAt;

    public Notification? Decide(IncidentChange change, AppPreferences preferences, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(preferences);
        if (change.Incident is not { } incident)
        {
            return null;
        }

        switch (change.Kind)
        {
            case IncidentChangeKind.Confirmed when preferences.NotifyOnOutage:
                if (!_announced.Add(incident.Id))
                {
                    return null;
                }

                _lastOutageNotifiedAt = nowUtc;
                return new Notification(
                    NotificationKind.Outage,
                    "Connection problem: " + IncidentText.Title(incident.Classification),
                    IncidentText.Explanation(incident.Classification));

            case IncidentChangeKind.Reopened when preferences.NotifyOnOutage:
                if (_lastOutageNotifiedAt is { } last && nowUtc - last < ReopenCooldown)
                {
                    // Same flapping problem; the earlier alert still stands.
                    return null;
                }

                _announced.Add(incident.Id);
                _lastOutageNotifiedAt = nowUtc;
                return new Notification(
                    NotificationKind.Outage,
                    "Connection problem again: " + IncidentText.Title(incident.Classification),
                    IncidentText.Explanation(incident.Classification));

            case IncidentChangeKind.Closed:
                if (_announced.Remove(incident.Id) && preferences.NotifyOnRecovery)
                {
                    var length = incident.EndedAtUtc is { } end ? DurationFormatter.Format((end - incident.StartedAtUtc).TotalSeconds) : "a while";
                    return new Notification(NotificationKind.Recovery, "Connection restored", "The problem lasted " + length + ".");
                }

                return null;

            case IncidentChangeKind.Interrupted:
                _announced.Remove(incident.Id);
                return null;

            default:
                return null;
        }
    }

    public Notification? Decide(MonitoringStatus status, AppPreferences preferences, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(preferences);

        var key = status.State == MonitoringState.Faulted
            ? "faulted"
            : status.LastError is { Length: > 0 } error ? "error:" + error : null;
        if (key is null)
        {
            _lastProblemKey = null;
            return null;
        }

        if (!preferences.NotifyOnMonitoringProblem
            || (key == _lastProblemKey && nowUtc - _lastProblemAt < MonitoringProblemCooldown))
        {
            return null;
        }

        _lastProblemKey = key;
        _lastProblemAt = nowUtc;
        return new Notification(
            NotificationKind.MonitoringProblem,
            "NetTrace monitoring problem",
            status.State == MonitoringState.Faulted
                ? "Monitoring stopped because of an error. Restart NetTrace; details are in the log."
                : status.LastError!);
    }
}
