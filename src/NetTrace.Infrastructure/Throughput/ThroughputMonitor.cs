using System.Net.NetworkInformation;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetTrace.Core.Network;
using NetTrace.Core.Throughput;
using NetTrace.Core.Time;

namespace NetTrace.Infrastructure.Throughput;

/// <summary>Reads cumulative 64-bit adapter byte counters. Sends no network traffic.</summary>
public sealed class SystemInterfaceCounterReader : IInterfaceCounterReader
{
    public CounterReading? Read(string interfaceId, DateTimeOffset atUtc)
    {
        try
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Id == interfaceId);
            if (nic is null)
            {
                return null;
            }

            var statistics = nic.GetIPStatistics();
            return new CounterReading(interfaceId, atUtc, statistics.BytesReceived, statistics.BytesSent);
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }
}

/// <summary>
/// Samples the tracked adapter's byte counters on a fixed interval and publishes a rate. The
/// connection is re-resolved on network-change events and periodically. Samples are display-only:
/// they are never persisted, exported or used for incident detection.
/// </summary>
public sealed class ThroughputMonitor : BackgroundService, IThroughputSource
{
    internal static readonly TimeSpan ConnectionRefreshInterval = TimeSpan.FromSeconds(30);

    private readonly INetworkInterfaceProvider _networkProvider;
    private readonly IInterfaceCounterReader _counters;
    private readonly IClock _clock;
    private readonly ILogger<ThroughputMonitor> _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private int _networkChanged;
    private ThroughputSample? _latest;

    public ThroughputMonitor(
        INetworkInterfaceProvider networkProvider,
        IInterfaceCounterReader counters,
        IClock clock,
        ILogger<ThroughputMonitor> logger)
        : this(networkProvider, counters, clock, logger, new ThroughputConfiguration(), Task.Delay)
    {
    }

    internal ThroughputMonitor(
        INetworkInterfaceProvider networkProvider,
        IInterfaceCounterReader counters,
        IClock clock,
        ILogger<ThroughputMonitor> logger,
        ThroughputConfiguration configuration,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        _networkProvider = networkProvider;
        _counters = counters;
        _clock = clock;
        _logger = logger;
        _delay = delay;
        Configuration = configuration;
    }

    public event EventHandler<ThroughputSample>? SampleAvailable;

    public ThroughputConfiguration Configuration { get; }

    public ThroughputSample? Latest => Volatile.Read(ref _latest);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        if (!Configuration.Enabled)
        {
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Clamp(Configuration.SampleIntervalSeconds, 1, 5));
        _networkProvider.NetworkChanged += OnNetworkChanged;
        try
        {
            NetworkContext? context = null;
            var contextAt = DateTimeOffset.MinValue;
            CounterReading? previous = null;

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var now = _clock.UtcNow;
                    if (context is null || Interlocked.Exchange(ref _networkChanged, 0) == 1 || now - contextAt >= ConnectionRefreshInterval)
                    {
                        context = await _networkProvider.GetCurrentAsync(stoppingToken).ConfigureAwait(false);
                        contextAt = now;
                    }

                    (var sample, previous) = TakeSample(context, previous, _clock.UtcNow);
                    Volatile.Write(ref _latest, sample);
                    SampleAvailable?.Invoke(this, sample);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Throughput is a convenience display; never let it take the app down.
                    _logger.LogWarning(ex, "Throughput sampling failed; showing unavailable.");
                    previous = null;
                }

                await _delay(interval, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        finally
        {
            _networkProvider.NetworkChanged -= OnNetworkChanged;
        }
    }

    internal (ThroughputSample Sample, CounterReading? Reading) TakeSample(
        NetworkContext context, CounterReading? previous, DateTimeOffset now)
    {
        var (nic, reason) = ThroughputInterfaceSelector.Select(context, Configuration.SelectedInterfaceId);
        if (nic is null || reason is not null)
        {
            return (ThroughputSample.Unavailable(now, nic, reason ?? "Unavailable"), null);
        }

        var reading = _counters.Read(nic.Id, now);
        if (reading is null)
        {
            return (ThroughputSample.Unavailable(now, nic, "Counters unavailable"), null);
        }

        var (down, up) = ThroughputCalculator.Rate(previous, reading);
        var sample = down is null || up is null
            ? ThroughputSample.Unavailable(now, nic, previous is null ? "Measuring" : "Counter reset")
            : new ThroughputSample(now, nic.Id, nic.Name, nic.Kind, down, up, null);
        return (sample, reading);
    }

    private void OnNetworkChanged(object? sender, EventArgs e) => Interlocked.Exchange(ref _networkChanged, 1);
}
