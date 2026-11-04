using NetTrace.Core.Incidents;

namespace NetTrace.Core.Monitoring;

/// <summary>Whether the monitoring engine is taking measurements. Serialized by name.</summary>
public enum MonitoringState
{
    Starting,
    Running,
    Disabled,
    Stopped,
    Faulted,
}

/// <summary>
/// Read-only view of the engine for the UI. <see cref="LastCycle"/> is null until the first
/// cycle completes; <see cref="LastSuccessfulMeasurementAtUtc"/> is null until any probe succeeds.
/// </summary>
public sealed record MonitoringStatus(
    MonitoringState State,
    MonitoringCycle? LastCycle,
    DateTimeOffset? LastSuccessfulMeasurementAtUtc,
    string? LastError,
    Incident? OpenIncident = null,
    bool IncidentSuspected = false)
{
    public static MonitoringStatus Initial { get; } = new(MonitoringState.Starting, null, null, null);
}

/// <summary>Lets the UI ask the engine to apply saved settings without restarting the application.</summary>
public interface IMonitoringControl
{
    /// <summary>Re-reads the stored configuration; the engine restarts its loop (or pauses if disabled).</summary>
    void ReloadConfiguration();
}

/// <summary>Raised on a background thread for every incident state change.</summary>
public interface IIncidentFeed
{
    event EventHandler<Incidents.IncidentChange>? IncidentChanged;
}

public interface IMonitoringStatusSource
{
    MonitoringStatus Current { get; }

    /// <summary>Raised on a background thread after each state change or completed cycle.</summary>
    event EventHandler<MonitoringStatus>? StatusChanged;
}
