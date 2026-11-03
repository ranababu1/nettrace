using NetTrace.Core.Monitoring;

namespace NetTrace.Core.Tests;

public class LifecycleReconcilerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid NewId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OldId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void FirstRun_RecordsStartWithoutGap()
    {
        var result = LifecycleReconciler.Reconcile(previous: null, NewId, Now);

        Assert.Null(result.Gap);
        var started = Assert.Single(result.Events);
        Assert.Equal(ApplicationEventType.ApplicationStarted, started.EventType);
        Assert.Equal(new LifecycleSession(NewId, Now, Now, null), result.NewSession);
    }

    [Fact]
    public void AfterCleanStop_GapIsApplicationNotRunningFromStopTime()
    {
        var stoppedAt = Now.AddHours(-8);
        var previous = new LifecycleSession(OldId, Now.AddDays(-1), stoppedAt.AddSeconds(-20), stoppedAt);

        var result = LifecycleReconciler.Reconcile(previous, NewId, Now);

        Assert.Equal(
            new MonitoringGap(stoppedAt, Now, MonitoringGapReason.ApplicationNotRunning, result.Gap!.Details, LifecycleReconciler.GapSource),
            result.Gap);
        Assert.Contains("unknown", result.Gap.Details, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Events, e => e.EventType == ApplicationEventType.UncleanShutdownDetected);
    }

    [Fact]
    public void AfterCrash_GapIsUnexpectedTerminationFromLastHeartbeat()
    {
        var lastAlive = Now.AddMinutes(-30);
        var previous = new LifecycleSession(OldId, Now.AddHours(-2), lastAlive, StoppedAtUtc: null);

        var result = LifecycleReconciler.Reconcile(previous, NewId, Now);

        Assert.NotNull(result.Gap);
        Assert.Equal(lastAlive, result.Gap.StartedAtUtc);
        Assert.Equal(Now, result.Gap.EndedAtUtc);
        Assert.Equal(MonitoringGapReason.UnexpectedTermination, result.Gap.Reason);
        var unclean = Assert.Single(result.Events, e => e.EventType == ApplicationEventType.UncleanShutdownDetected);
        Assert.Equal(OldId.ToString("D"), unclean.Details!["previous_session_id"]);
        Assert.Equal(ApplicationEventType.ApplicationStarted, result.Events[^1].EventType);
    }

    [Fact]
    public void ClockMovedBackwards_RecordsDiscontinuityInsteadOfNegativeGap()
    {
        var previous = new LifecycleSession(OldId, Now.AddHours(1), Now.AddHours(2), Now.AddHours(2));

        var result = LifecycleReconciler.Reconcile(previous, NewId, Now);

        Assert.Null(result.Gap);
        Assert.Contains(result.Events, e => e.EventType == ApplicationEventType.ClockDiscontinuityDetected);
    }

    [Fact]
    public void ImmediateRestart_RecordsNoZeroLengthGap()
    {
        var previous = new LifecycleSession(OldId, Now.AddHours(-1), Now, Now);

        var result = LifecycleReconciler.Reconcile(previous, NewId, Now);

        Assert.Null(result.Gap);
    }
}
