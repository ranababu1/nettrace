using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetTrace.Core.Configuration;
using NetTrace.Core.Monitoring;
using NetTrace.Core.Network;
using NetTrace.Core.Time;

namespace NetTrace.Infrastructure.Monitoring;

/// <summary>
/// The background monitoring loop. One cycle runs at a time on a fixed-rate schedule; missed
/// slots (stalls, sleep) become monitoring gaps of unknown cause, and disabled monitoring is an
/// explicit gap. Runs independently of any window.
/// </summary>
public sealed class MonitoringService : BackgroundService, IMonitoringStatusSource
{
    /// <summary>Network context is re-read at least this often even without change events.</summary>
    public static readonly TimeSpan NetworkReconciliationInterval = TimeSpan.FromSeconds(60);

    internal const string GapSource = "MonitoringService";

    private readonly IMonitoringConfigurationStore _configurationStore;
    private readonly INetworkInterfaceProvider _networkProvider;
    private readonly MonitoringCycleRunner _runner;
    private readonly IObservationRepository _observations;
    private readonly IMonitoringGapRepository _gaps;
    private readonly IPowerEvents _powerEvents;
    private readonly IClock _clock;
    private readonly ILogger<MonitoringService> _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private int _networkChanged;
    private long _suspendedAtTicks;
    private MonitoringStatus _status = MonitoringStatus.Initial;

    public MonitoringService(
        IMonitoringConfigurationStore configurationStore,
        INetworkInterfaceProvider networkProvider,
        MonitoringCycleRunner runner,
        IObservationRepository observations,
        IMonitoringGapRepository gaps,
        IPowerEvents powerEvents,
        IClock clock,
        ILogger<MonitoringService> logger)
        : this(configurationStore, networkProvider, runner, observations, gaps, powerEvents, clock, logger, Task.Delay)
    {
    }

    internal MonitoringService(
        IMonitoringConfigurationStore configurationStore,
        INetworkInterfaceProvider networkProvider,
        MonitoringCycleRunner runner,
        IObservationRepository observations,
        IMonitoringGapRepository gaps,
        IPowerEvents powerEvents,
        IClock clock,
        ILogger<MonitoringService> logger,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        _configurationStore = configurationStore;
        _networkProvider = networkProvider;
        _runner = runner;
        _observations = observations;
        _gaps = gaps;
        _powerEvents = powerEvents;
        _clock = clock;
        _logger = logger;
        _delay = delay;
    }

    public event EventHandler<MonitoringStatus>? StatusChanged;

    public MonitoringStatus Current => Volatile.Read(ref _status);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Leave host start-up immediately; all monitoring work happens on the thread pool.
        await Task.Yield();
        try
        {
            var stored = await _configurationStore.LoadAsync(stoppingToken).ConfigureAwait(false);
            if (stored.Configuration.MonitoringEnabled)
            {
                await RunMonitoringLoopAsync(stored.Configuration, stored.Version, stoppingToken).ConfigureAwait(false);
            }
            else
            {
                await WaitWhileDisabledAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "The monitoring engine stopped because of an unexpected error.");
            Publish(Current with { State = MonitoringState.Faulted, LastError = "The monitoring engine stopped unexpectedly." });
            return;
        }

        Publish(Current with { State = MonitoringState.Stopped });
    }

    private async Task RunMonitoringLoopAsync(MonitoringConfiguration configuration, int version, CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(configuration.IntervalSeconds);
        _networkProvider.NetworkChanged += OnNetworkChanged;
        _powerEvents.Suspending += OnSuspending;
        _powerEvents.Resumed += OnResumed;
        try
        {
            var context = await _networkProvider.GetCurrentAsync(stoppingToken).ConfigureAwait(false);
            var contextRefreshedAt = _clock.UtcNow;
            NetworkContext? lastRecordedContext = null;
            var scheduledAt = _clock.UtcNow;

            _logger.LogInformation(
                "Monitoring started: every {IntervalSeconds} s, configuration version {ConfigurationVersion}.",
                configuration.IntervalSeconds,
                version);
            Publish(Current with { State = MonitoringState.Running });

            while (!stoppingToken.IsCancellationRequested)
            {
                if (Interlocked.Exchange(ref _networkChanged, 0) == 1
                    || _clock.UtcNow - contextRefreshedAt >= NetworkReconciliationInterval)
                {
                    context = await _networkProvider.GetCurrentAsync(stoppingToken).ConfigureAwait(false);
                    contextRefreshedAt = _clock.UtcNow;
                }

                var cycle = await _runner.RunAsync(configuration, version, context, scheduledAt, stoppingToken).ConfigureAwait(false);
                lastRecordedContext = await SaveCycleAsync(cycle, lastRecordedContext, stoppingToken).ConfigureAwait(false);

                var slot = CycleSchedule.Next(scheduledAt, _clock.UtcNow, interval);
                if (slot.ClockMovedBackwards)
                {
                    _logger.LogWarning("The system clock moved backwards; the monitoring schedule was reset.");
                }
                else if (slot.SkippedSlots > 0)
                {
                    await RecordMissedSlotsAsync(scheduledAt + interval, slot, stoppingToken).ConfigureAwait(false);
                }

                var wait = slot.NextScheduledAtUtc - _clock.UtcNow;
                if (wait > TimeSpan.Zero)
                {
                    await _delay(wait, stoppingToken).ConfigureAwait(false);
                }

                scheduledAt = slot.NextScheduledAtUtc;
            }
        }
        finally
        {
            _networkProvider.NetworkChanged -= OnNetworkChanged;
            _powerEvents.Suspending -= OnSuspending;
            _powerEvents.Resumed -= OnResumed;
        }
    }

    private async Task<NetworkContext?> SaveCycleAsync(
        MonitoringCycle cycle, NetworkContext? lastRecordedContext, CancellationToken stoppingToken)
    {
        var recordSnapshot = NetworkContextChange.IsMaterial(lastRecordedContext, cycle.NetworkContext);
        var lastSuccess = cycle.Results.Where(r => r.Succeeded).Select(r => (DateTimeOffset?)r.CompletedAtUtc).Max()
            ?? Current.LastSuccessfulMeasurementAtUtc;
        try
        {
            await _observations.SaveCycleAsync(cycle, recordSnapshot, stoppingToken).ConfigureAwait(false);
            Publish(Current with { LastCycle = cycle, LastSuccessfulMeasurementAtUtc = lastSuccess, LastError = null });
            return recordSnapshot ? cycle.NetworkContext : lastRecordedContext;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Keep measuring; a later cycle may succeed. The lost cycle is visible in the log.
            _logger.LogError(ex, "Failed to save monitoring cycle {CycleId}.", cycle.CycleId);
            Publish(Current with { LastCycle = cycle, LastSuccessfulMeasurementAtUtc = lastSuccess, LastError = "Measurements could not be saved to the database." });
            return lastRecordedContext;
        }
    }

    private async Task RecordMissedSlotsAsync(DateTimeOffset firstMissedSlot, CycleSlot slot, CancellationToken stoppingToken)
    {
        var gapEnd = _clock.UtcNow;
        var suspendedAtTicks = Interlocked.Exchange(ref _suspendedAtTicks, 0);
        var slept = suspendedAtTicks != 0 && new DateTimeOffset(suspendedAtTicks, TimeSpan.Zero) <= gapEnd;
        var count = slot.SkippedSlots.ToString(CultureInfo.InvariantCulture);
        var details = slept
            ? $"{count} scheduled monitoring cycles were missed because the system was asleep; network conditions during this period are unknown."
            : $"{count} scheduled monitoring cycles were missed. The system may have been asleep or unresponsive; network conditions during this period are unknown.";
        if (gapEnd <= firstMissedSlot)
        {
            return;
        }

        try
        {
            await _gaps.AddAsync(new MonitoringGap(firstMissedSlot, gapEnd, slept ? MonitoringGapReason.SystemSleep : MonitoringGapReason.Unknown, details, GapSource), stoppingToken)
                .ConfigureAwait(false);
            _logger.LogWarning("Recorded {SkippedSlots} missed monitoring cycles as a gap.", slot.SkippedSlots);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to record a monitoring gap for missed cycles.");
        }
    }

    private async Task WaitWhileDisabledAsync(CancellationToken stoppingToken)
    {
        var gapId = await _gaps.AddAsync(
            new MonitoringGap(
                _clock.UtcNow,
                EndedAtUtc: null,
                MonitoringGapReason.MonitoringDisabled,
                "Monitoring is turned off in settings. Network conditions during this period are unknown.",
                GapSource),
            stoppingToken).ConfigureAwait(false);
        _logger.LogInformation("Monitoring is disabled in settings.");
        Publish(Current with { State = MonitoringState.Disabled });

        try
        {
            await _delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
        }
        finally
        {
            // Close with a token that is not already cancelled so shutdown still records the end.
            await _gaps.CloseAsync(gapId, _clock.UtcNow, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private void OnSuspending(object? sender, EventArgs e) =>
        Interlocked.Exchange(ref _suspendedAtTicks, _clock.UtcNow.UtcTicks);

    private void OnResumed(object? sender, EventArgs e) => Interlocked.Exchange(ref _networkChanged, 1);

    private void OnNetworkChanged(object? sender, EventArgs e) => Interlocked.Exchange(ref _networkChanged, 1);

    private void Publish(MonitoringStatus status)
    {
        Volatile.Write(ref _status, status);
        StatusChanged?.Invoke(this, status);
    }
}
