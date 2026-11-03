using Microsoft.Extensions.Logging.Abstractions;
using NetTrace.Core.Configuration;
using NetTrace.Core.Monitoring;
using NetTrace.Core.Network;
using NetTrace.Core.Probes;
using NetTrace.Infrastructure.Monitoring;

namespace NetTrace.Infrastructure.Tests;

public class MonitoringServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Runs_CyclesOnSchedule_AndRecordsSnapshotOnlyWhenContextChanges()
    {
        var harness = new Harness(cyclesBeforeStop: 3);

        await harness.RunAsync();

        Assert.Equal(3, harness.Observations.Saved.Count);
        Assert.Equal([T0, T0.AddSeconds(5), T0.AddSeconds(10)], harness.Observations.Saved.Select(s => s.Cycle.ScheduledAtUtc));
        Assert.Equal([true, false, false], harness.Observations.Saved.Select(s => s.Snapshot));
        Assert.All(harness.Observations.Saved, s => Assert.Equal(4, s.Cycle.ConfigurationVersion));
        Assert.Equal(MonitoringState.Stopped, harness.Service.Current.State);
        Assert.NotNull(harness.Service.Current.LastCycle);
        Assert.NotNull(harness.Service.Current.LastSuccessfulMeasurementAtUtc);
        Assert.Empty(harness.Gaps.Gaps);
    }

    [Fact]
    public async Task NetworkChange_RefreshesContextAndRecordsNewSnapshot()
    {
        var harness = new Harness(cyclesBeforeStop: 3);
        harness.OnDelay = count =>
        {
            if (count == 1)
            {
                harness.Network.Gateway = "10.0.0.1";
                harness.Network.RaiseChanged();
            }
        };

        await harness.RunAsync();

        Assert.Equal([true, true, false], harness.Observations.Saved.Select(s => s.Snapshot));
        Assert.Equal("10.0.0.1", harness.Observations.Saved[^1].Cycle.NetworkContext.DefaultGateway);
    }

    [Fact]
    public async Task MissedSlots_AreRecordedAsUnknownGap()
    {
        var harness = new Harness(cyclesBeforeStop: 2);
        // Simulate the machine stalling for 32 s after the first cycle (e.g. sleep).
        harness.BeforeFirstScheduleCheck = () => harness.Clock.UtcNow = harness.Clock.UtcNow.AddSeconds(32);

        await harness.RunAsync();

        var gap = Assert.Single(harness.Gaps.Gaps);
        Assert.Equal(MonitoringGapReason.Unknown, gap.Reason);
        Assert.Equal(T0.AddSeconds(5), gap.StartedAtUtc);
        Assert.Equal(T0.AddSeconds(32), gap.EndedAtUtc);
        Assert.Contains("6 scheduled monitoring cycles were missed", gap.Details, StringComparison.Ordinal);
        Assert.Equal(T0.AddSeconds(35), harness.Observations.Saved[1].Cycle.ScheduledAtUtc);
    }

    [Fact]
    public async Task MissedSlots_AfterObservedSuspend_AreRecordedAsSystemSleep_AndRefreshNetwork()
    {
        var harness = new Harness(cyclesBeforeStop: 2);
        harness.BeforeFirstScheduleCheck = () =>
        {
            harness.Power.Suspend();
            harness.Clock.UtcNow = harness.Clock.UtcNow.AddSeconds(32);
            harness.Network.Gateway = "10.0.0.1";
            harness.Power.Resume();
        };

        await harness.RunAsync();

        var gap = Assert.Single(harness.Gaps.Gaps);
        Assert.Equal(MonitoringGapReason.SystemSleep, gap.Reason);
        Assert.Contains("asleep", gap.Details, StringComparison.Ordinal);
        Assert.Equal("10.0.0.1", harness.Observations.Saved[^1].Cycle.NetworkContext.DefaultGateway);
    }

    [Fact]
    public async Task Disabled_RecordsOpenGapClosedOnStop_AndProbesNothing()
    {
        var harness = new Harness(cyclesBeforeStop: 1, new MonitoringConfiguration { MonitoringEnabled = false });

        await harness.Service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => harness.Service.Current.State == MonitoringState.Disabled);
        harness.Clock.UtcNow = T0.AddMinutes(10);
        await harness.Service.StopAsync(CancellationToken.None);

        var gap = Assert.Single(harness.Gaps.Gaps);
        Assert.Equal(MonitoringGapReason.MonitoringDisabled, gap.Reason);
        Assert.Equal(T0, gap.StartedAtUtc);
        Assert.Equal(T0.AddMinutes(10), gap.EndedAtUtc);
        Assert.Empty(harness.Observations.Saved);
        Assert.Equal(0, harness.Probe.CallCount);
    }

    [Fact]
    public async Task SaveFailure_KeepsMonitoringAndReportsError()
    {
        var harness = new Harness(cyclesBeforeStop: 2);
        harness.Observations.FailNext = true;

        await harness.RunAsync();

        Assert.Single(harness.Observations.Saved);
        Assert.Equal(2, harness.Observations.Attempts);
        // The failed first save did not count as recorded, so the next cycle stores the snapshot.
        Assert.True(harness.Observations.Saved[0].Snapshot);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the monitoring service.");
            await Task.Delay(10);
        }
    }

    private sealed class Harness
    {
        private readonly int _cyclesBeforeStop;
        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _delays;

        public Harness(int cyclesBeforeStop, MonitoringConfiguration? configuration = null)
        {
            _cyclesBeforeStop = cyclesBeforeStop;
            Network = new FakeNetwork(Clock);
            var config = new FakeConfigStore(configuration ?? new MonitoringConfiguration(), version: 4);
            Service = new MonitoringService(
                config,
                Network,
                new MonitoringCycleRunner(Probe, Clock),
                Observations,
                Gaps,
                Power,
                Clock,
                NullLogger<MonitoringService>.Instance,
                DelayAsync);
            Observations.AfterSave = () =>
            {
                if (BeforeFirstScheduleCheck is { } stall)
                {
                    BeforeFirstScheduleCheck = null;
                    stall();
                }
            };
        }

        public FakeClock Clock { get; } = new(T0);

        public FakeNetwork Network { get; }

        public FakeProbe Probe { get; } = new();

        public FakeObservations Observations { get; } = new();

        public FakeGaps Gaps { get; } = new();

        public FakePower Power { get; } = new();

        public MonitoringService Service { get; }

        public Action<int>? OnDelay { get; set; }

        public Action? BeforeFirstScheduleCheck { get; set; }

        public async Task RunAsync()
        {
            await Service.StartAsync(CancellationToken.None);
            await _done.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Service.StopAsync(CancellationToken.None);
        }

        private async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            if (delay == Timeout.InfiniteTimeSpan)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return;
            }

            Clock.UtcNow += delay;
            var count = ++_delays;
            OnDelay?.Invoke(count);
            if (Observations.Attempts >= _cyclesBeforeStop)
            {
                _done.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
        }
    }

    private sealed class FakePower : IPowerEvents
    {
        public event EventHandler? Suspending;

        public event EventHandler? Resumed;

        public void Suspend() => Suspending?.Invoke(this, EventArgs.Empty);

        public void Resume() => Resumed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class FakeConfigStore(MonitoringConfiguration configuration, int version) : IMonitoringConfigurationStore
    {
        public Task<StoredMonitoringConfiguration> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new StoredMonitoringConfiguration(configuration, version, null));

        public Task<int> SaveAsync(MonitoringConfiguration configuration, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeNetwork(FakeClock clock) : INetworkInterfaceProvider
    {
        public event EventHandler? NetworkChanged;

        public string Gateway { get; set; } = "192.168.1.1";

        public void RaiseChanged() => NetworkChanged?.Invoke(this, EventArgs.Empty);

        public Task<NetworkContext> GetCurrentAsync(CancellationToken cancellationToken = default)
        {
            var nic = new NetworkInterfaceSnapshot(
                "nic", "Wi-Fi", "Test", InterfaceKind.Wifi, InterfaceStatus.Up, 12, ["192.168.1.20"], [], [Gateway], [Gateway]);
            return Task.FromResult(new NetworkContext(clock.UtcNow, [nic], nic, Gateway));
        }
    }

    private sealed class FakeProbe : IIcmpProbe
    {
        private int _calls;

        public int CallCount => Volatile.Read(ref _calls);

        public Task<ProbeResult> PingAsync(ProbeType probeType, string target, int timeoutMilliseconds, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new ProbeResult(
                probeType, target, "fake", T0, T0.AddMilliseconds(10), ProbeOutcome.Success, 10, timeoutMilliseconds, null, null));
        }
    }

    private sealed class FakeObservations : IObservationRepository
    {
        public List<(MonitoringCycle Cycle, bool Snapshot)> Saved { get; } = [];

        public int Attempts { get; private set; }

        public bool FailNext { get; set; }

        public Action? AfterSave { get; set; }

        public Task SaveCycleAsync(MonitoringCycle cycle, bool recordNetworkSnapshot, CancellationToken cancellationToken = default)
        {
            Attempts++;
            if (FailNext)
            {
                FailNext = false;
                throw new IOException("disk full");
            }

            Saved.Add((cycle, recordNetworkSnapshot));
            AfterSave?.Invoke();
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<StoredObservation>> GetObservationsAsync(
            DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeGaps : IMonitoringGapRepository
    {
        public List<MonitoringGap> Gaps { get; } = [];

        public Task<long> AddAsync(MonitoringGap gap, CancellationToken cancellationToken = default)
        {
            Gaps.Add(gap);
            return Task.FromResult((long)Gaps.Count);
        }

        public Task CloseAsync(long gapId, DateTimeOffset endedAtUtc, CancellationToken cancellationToken = default)
        {
            var index = (int)gapId - 1;
            Gaps[index] = Gaps[index] with { EndedAtUtc = endedAtUtc };
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<MonitoringGap>> GetOverlappingAsync(
            DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MonitoringGap>>(Gaps);
    }
}
