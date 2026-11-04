using NetTrace.Core.Configuration;
using NetTrace.Core.Incidents;
using NetTrace.Core.Monitoring;
using NetTrace.Core.Notifications;

namespace NetTrace.Core.Tests;

public class NotificationPolicyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly AppPreferences Prefs = new();

    [Fact]
    public void ConfirmedIncident_IsAnnouncedOnce_AndRestoredFollowsWithDuration()
    {
        var policy = new NotificationPolicy();
        var open = Incident(null);

        var outage = policy.Decide(Change(IncidentChangeKind.Confirmed, open), Prefs, T0);
        Assert.Null(policy.Decide(Change(IncidentChangeKind.Confirmed, open), Prefs, T0.AddSeconds(5)));
        var restored = policy.Decide(Change(IncidentChangeKind.Closed, open with { EndedAtUtc = T0.AddMinutes(3) }), Prefs, T0.AddMinutes(4));

        Assert.Equal(NotificationKind.Outage, outage!.Kind);
        Assert.Contains("Internet not reachable", outage.Title, StringComparison.Ordinal);
        Assert.Contains("Your router responded", outage.Message, StringComparison.Ordinal);
        Assert.Equal(NotificationKind.Recovery, restored!.Kind);
        Assert.Contains("3 min", restored.Message, StringComparison.Ordinal);
        Assert.Null(policy.Decide(Change(IncidentChangeKind.Closed, open with { EndedAtUtc = T0.AddMinutes(3) }), Prefs, T0.AddMinutes(5)));
    }

    [Fact]
    public void Interruption_IsNeverReportedAsRecovery()
    {
        var policy = new NotificationPolicy();
        var open = Incident(null);
        policy.Decide(Change(IncidentChangeKind.Confirmed, open), Prefs, T0);

        Assert.Null(policy.Decide(Change(IncidentChangeKind.Interrupted, open with { EndedAtUtc = T0.AddMinutes(1) }), Prefs, T0.AddMinutes(1)));
        Assert.Null(policy.Decide(Change(IncidentChangeKind.Closed, open with { EndedAtUtc = T0.AddMinutes(2) }), Prefs, T0.AddMinutes(2)));
    }

    [Fact]
    public void RecoveryIsOnlySentForAnnouncedIncidents()
    {
        var policy = new NotificationPolicy();
        var open = Incident(null);

        var result = policy.Decide(Change(IncidentChangeKind.Closed, open with { EndedAtUtc = T0.AddMinutes(1) }), Prefs, T0);

        Assert.Null(result);
    }

    [Fact]
    public void QuickReopen_DoesNotReAlertWithinTheCooldown_ButLaterOneDoes()
    {
        var policy = new NotificationPolicy();
        var open = Incident(null);
        policy.Decide(Change(IncidentChangeKind.Confirmed, open), Prefs, T0);

        Assert.Null(policy.Decide(Change(IncidentChangeKind.Reopened, open), Prefs, T0.AddMinutes(2)));
        var later = policy.Decide(Change(IncidentChangeKind.Reopened, open), Prefs, T0.AddMinutes(6));

        Assert.Equal(NotificationKind.Outage, later!.Kind);
        Assert.StartsWith("Connection problem again", later.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void DisabledPreferences_SuppressTheirNotifications()
    {
        var policy = new NotificationPolicy();
        var open = Incident(null);
        var noOutage = new AppPreferences { NotifyOnOutage = false };

        Assert.Null(policy.Decide(Change(IncidentChangeKind.Confirmed, open), noOutage, T0));
        // Never announced, so no recovery either.
        Assert.Null(policy.Decide(Change(IncidentChangeKind.Closed, open with { EndedAtUtc = T0.AddMinutes(1) }), Prefs, T0.AddMinutes(1)));

        var noRecovery = new AppPreferences { NotifyOnRecovery = false };
        policy.Decide(Change(IncidentChangeKind.Confirmed, open), noRecovery, T0.AddMinutes(2));
        Assert.Null(policy.Decide(Change(IncidentChangeKind.Closed, open with { EndedAtUtc = T0.AddMinutes(3) }), noRecovery, T0.AddMinutes(3)));
    }

    [Fact]
    public void SuspectedChanges_NeverNotify()
    {
        var policy = new NotificationPolicy();

        Assert.Null(policy.Decide(new IncidentChange(IncidentChangeKind.SuspectedStarted, T0, null, "x"), Prefs, T0));
        Assert.Null(policy.Decide(new IncidentChange(IncidentChangeKind.SuspectedCleared, T0, null, "x"), Prefs, T0));
    }

    [Fact]
    public void MonitoringProblem_IsAnnouncedOnce_RepeatsAfterCooldown_AndResetsWhenFixed()
    {
        var policy = new NotificationPolicy();
        var bad = MonitoringStatus.Initial with { State = MonitoringState.Running, LastError = "Measurements could not be saved to the database." };
        var good = bad with { LastError = null };

        var first = policy.Decide(bad, Prefs, T0);
        Assert.Equal(NotificationKind.MonitoringProblem, first!.Kind);
        Assert.Null(policy.Decide(bad, Prefs, T0.AddMinutes(5)));
        Assert.NotNull(policy.Decide(bad, Prefs, T0.AddMinutes(31)));

        Assert.Null(policy.Decide(good, Prefs, T0.AddMinutes(32)));
        Assert.NotNull(policy.Decide(bad, Prefs, T0.AddMinutes(33)));
    }

    [Fact]
    public void FaultedEngine_Notifies_UnlessDisabled()
    {
        var faulted = MonitoringStatus.Initial with { State = MonitoringState.Faulted };

        Assert.Contains("Restart NetTrace", new NotificationPolicy().Decide(faulted, Prefs, T0)!.Message, StringComparison.Ordinal);
        Assert.Null(new NotificationPolicy().Decide(faulted, new AppPreferences { NotifyOnMonitoringProblem = false }, T0));
    }

    private static IncidentChange Change(IncidentChangeKind kind, Incident incident) => new(kind, T0, incident, null);

    private static readonly Guid Id = Guid.NewGuid();

    private static Incident Incident(DateTimeOffset? end) =>
        new(Id, T0, T0.AddSeconds(15), end, IncidentClassification.ExternalConnectivityUnavailable,
            ClassificationConfidence.High, "s", [], []);
}
