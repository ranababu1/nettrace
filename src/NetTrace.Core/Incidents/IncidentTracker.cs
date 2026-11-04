using NetTrace.Core.Configuration;
using NetTrace.Core.Monitoring;

namespace NetTrace.Core.Incidents;

/// <summary>
/// Turns a stream of cycles into incidents: a run of failing cycles is only suspected until it
/// reaches the confirmation threshold; a confirmed incident closes only after the recovery
/// threshold of consecutive healthy cycles; a failure that resumes within the merge window
/// reopens the previous incident rather than starting a new one. Not thread-safe; feed it from
/// one loop. Deterministic: all time comes from the cycles.
/// </summary>
public sealed class IncidentTracker
{
    private readonly MonitoringConfiguration _configuration;
    private SlowProbeEvidence _slow = SlowProbeEvidence.None;

    private int _failStreak;
    private DateTimeOffset _streakStartedAt;
    private DateTimeOffset _lastFailingAt;

    private int _healthyStreak;
    private DateTimeOffset _recoveryStartedAt;

    private Incident? _lastClosed;

    public IncidentTracker(MonitoringConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _configuration = configuration;
    }

    /// <summary>The open confirmed incident, if any.</summary>
    public Incident? Open { get; private set; }

    /// <summary>True while failing cycles have been seen but the threshold is not yet reached.</summary>
    public bool IsSuspected => Open is null && _failStreak > 0;

    public IReadOnlyList<IncidentChange> Observe(MonitoringCycle cycle)
    {
        ArgumentNullException.ThrowIfNull(cycle);
        _slow = _slow.Update(cycle);
        var assessment = CycleClassifier.Assess(cycle, _slow);
        var changes = new List<IncidentChange>();

        switch (assessment.Health)
        {
            case CycleHealth.Failing:
                OnFailing(cycle, assessment, changes);
                break;
            case CycleHealth.Healthy:
                OnHealthy(cycle, changes);
                break;
        }

        return changes;
    }

    /// <summary>
    /// Monitoring was interrupted (sleep, shutdown, disabled). Network conditions during a gap
    /// are unknown, so a suspicion is dropped and an open incident ends at its last failing cycle.
    /// </summary>
    public IReadOnlyList<IncidentChange> ObserveGap(DateTimeOffset gapStartedAtUtc)
    {
        var changes = new List<IncidentChange>();
        if (Open is { } open)
        {
            Close(open, _lastFailingAt, changes, IncidentChangeKind.Interrupted);
        }
        else if (_failStreak > 0)
        {
            changes.Add(new IncidentChange(IncidentChangeKind.SuspectedCleared, gapStartedAtUtc, null, "Monitoring was interrupted."));
        }

        _failStreak = 0;
        _healthyStreak = 0;
        _lastClosed = null;
        return changes;
    }

    private void OnFailing(MonitoringCycle cycle, CycleAssessment assessment, List<IncidentChange> changes)
    {
        _healthyStreak = 0;
        _lastFailingAt = cycle.CompletedAtUtc;

        if (Open is not null)
        {
            return;
        }

        if (_failStreak == 0)
        {
            _streakStartedAt = cycle.StartedAtUtc;
            changes.Add(new IncidentChange(IncidentChangeKind.SuspectedStarted, cycle.CompletedAtUtc, null, assessment.Summary));
        }

        _failStreak++;
        if (_failStreak >= _configuration.OutageConfirmationThreshold)
        {
            Confirm(cycle, assessment, changes);
        }
    }

    private void Confirm(MonitoringCycle cycle, CycleAssessment assessment, List<IncidentChange> changes)
    {
        var merge = _lastClosed is { EndedAtUtc: { } endedAt }
            && _streakStartedAt - endedAt <= TimeSpan.FromSeconds(_configuration.IncidentMergeWindowSeconds);

        if (merge)
        {
            Open = _lastClosed! with { EndedAtUtc = null };
            changes.Add(new IncidentChange(IncidentChangeKind.Reopened, cycle.CompletedAtUtc, Open, assessment.Summary));
        }
        else
        {
            Open = new Incident(
                Guid.NewGuid(),
                _streakStartedAt,
                cycle.CompletedAtUtc,
                null,
                assessment.Classification!.Value,
                assessment.Confidence,
                assessment.Summary,
                assessment.FailedProbes,
                assessment.WorkingProbes);
            changes.Add(new IncidentChange(IncidentChangeKind.Confirmed, cycle.CompletedAtUtc, Open, assessment.Summary));
        }

        _lastClosed = null;
    }

    private void OnHealthy(MonitoringCycle cycle, List<IncidentChange> changes)
    {
        if (Open is { } open)
        {
            if (_healthyStreak == 0)
            {
                _recoveryStartedAt = cycle.StartedAtUtc;
            }

            _healthyStreak++;
            if (_healthyStreak >= _configuration.RecoveryConfirmationThreshold)
            {
                Close(open, _recoveryStartedAt, changes);
            }

            return;
        }

        if (_failStreak > 0)
        {
            changes.Add(new IncidentChange(IncidentChangeKind.SuspectedCleared, cycle.CompletedAtUtc, null, "Probes recovered before the outage threshold."));
            _failStreak = 0;
        }
    }

    private void Close(Incident open, DateTimeOffset endedAt, List<IncidentChange> changes, IncidentChangeKind kind = IncidentChangeKind.Closed)
    {
        var closed = open with { EndedAtUtc = endedAt };
        _lastClosed = closed;
        Open = null;
        _failStreak = 0;
        _healthyStreak = 0;
        changes.Add(new IncidentChange(kind, endedAt, closed, null));
    }
}
