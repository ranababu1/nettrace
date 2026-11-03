using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetTrace.Core.Monitoring;
using NetTrace.Core.Time;

namespace NetTrace.Infrastructure.Lifecycle;

/// <summary>
/// Records application start and stop, reconciles the previous run into a monitoring gap, and
/// keeps a heartbeat so that an unexpected termination can be dated on the next launch.
/// </summary>
public sealed class ApplicationLifecycleService(
    SqliteLifecycleStore store,
    IClock clock,
    ILogger<ApplicationLifecycleService> logger) : IHostedService, IDisposable
{
    /// <summary>Upper bound on how much of a crashed run's final moments is lost to the gap.</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    private readonly CancellationTokenSource _stopping = new();
    private LifecycleSession? _session;
    private Task? _heartbeatTask;

    public LifecycleSession? CurrentSession => _session;

    public Task StartAsync(CancellationToken cancellationToken) =>
        // Run off the caller's thread: host start is awaited from the WPF dispatcher.
        Task.Run(() => StartCoreAsync(cancellationToken), cancellationToken);

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_heartbeatTask is not null)
        {
            await _heartbeatTask.ConfigureAwait(false);
        }

        if (_session is null)
        {
            return;
        }

        var now = clock.UtcNow;
        var stopped = _session with { LastAliveAtUtc = now, StoppedAtUtc = now };
        await store.RecordStopAsync(
            stopped,
            new ApplicationEvent(now, ApplicationEventType.ApplicationStopped, new Dictionary<string, string?>
            {
                ["session_id"] = stopped.SessionId.ToString("D"),
            }),
            cancellationToken).ConfigureAwait(false);
        _session = stopped;
        logger.LogInformation("Recorded clean application stop for session {SessionId}.", stopped.SessionId);
    }

    public void Dispose() => _stopping.Dispose();

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        var previous = await store.LoadSessionAsync(cancellationToken).ConfigureAwait(false);
        var reconciliation = LifecycleReconciler.Reconcile(previous, Guid.NewGuid(), clock.UtcNow);
        await store.ApplyStartupAsync(reconciliation, cancellationToken).ConfigureAwait(false);
        _session = reconciliation.NewSession;

        if (reconciliation.Gap is { } gap)
        {
            logger.LogInformation(
                "Recorded monitoring gap {GapReason} from {GapStartUtc} to {GapEndUtc}.",
                gap.Reason,
                gap.StartedAtUtc,
                gap.EndedAtUtc);
        }

        if (reconciliation.Events.Any(e => e.EventType == ApplicationEventType.UncleanShutdownDetected))
        {
            logger.LogWarning("The previous NetTrace session {SessionId} ended unexpectedly.", previous?.SessionId);
        }

        _heartbeatTask = HeartbeatLoopAsync(_stopping.Token);
    }

    private async Task HeartbeatLoopAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(HeartbeatInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                var beat = _session! with { LastAliveAtUtc = clock.UtcNow };
                try
                {
                    await store.UpdateSessionAsync(beat, stoppingToken).ConfigureAwait(false);
                    _session = beat;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A missed heartbeat only widens a possible future gap; keep monitoring running.
                    logger.LogWarning(ex, "Failed to record lifecycle heartbeat.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }
}
