using NetTrace.Core.Configuration;
using NetTrace.Core.Monitoring;
using NetTrace.Core.Network;
using NetTrace.Core.Probes;
using NetTrace.Core.Time;

namespace NetTrace.Core.Tests;

public class MonitoringCycleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Five = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Run_ProbesGatewayAndEveryExternalTargetWithConfiguredTimeouts()
    {
        var probe = new FakeIcmpProbe();
        var runner = new MonitoringCycleRunner(probe, new FixedClock(T0));

        var cycle = await runner.RunAsync(new MonitoringConfiguration(), 3, Context("192.168.1.1"), T0, CancellationToken.None);

        Assert.Equal(3, cycle.ConfigurationVersion);
        Assert.Equal(T0, cycle.ScheduledAtUtc);
        Assert.Collection(
            cycle.Results,
            r => Assert.Equal((ProbeType.Gateway, "192.168.1.1", 1000), (r.ProbeType, r.Target, r.TimeoutMilliseconds)),
            r => Assert.Equal((ProbeType.ExternalIcmp, "1.1.1.1", 2000), (r.ProbeType, r.Target, r.TimeoutMilliseconds)),
            r => Assert.Equal((ProbeType.ExternalIcmp, "8.8.8.8", 2000), (r.ProbeType, r.Target, r.TimeoutMilliseconds)));
        Assert.All(cycle.Results, r => Assert.True(r.Succeeded));
    }

    [Fact]
    public async Task Run_NoGateway_RecordsSkippedGatewayAndStillProbesExternally()
    {
        var probe = new FakeIcmpProbe();
        var runner = new MonitoringCycleRunner(probe, new FixedClock(T0));

        var cycle = await runner.RunAsync(new MonitoringConfiguration(), 1, Context(gateway: null), T0, CancellationToken.None);

        var gateway = Assert.Single(cycle.Results, r => r.ProbeType == ProbeType.Gateway);
        Assert.Equal(ProbeOutcome.Skipped, gateway.Outcome);
        Assert.Equal("NoActiveConnection", gateway.ErrorCode);
        Assert.Null(gateway.LatencyMs);
        Assert.Equal(2, cycle.Results.Count(r => r.ProbeType == ProbeType.ExternalIcmp));
        Assert.DoesNotContain(probe.Calls, c => c.Type == ProbeType.Gateway);
    }

    [Fact]
    public async Task Run_ProbeThrowing_IsRecordedAsErrorWithoutLosingOtherResults()
    {
        var probe = new FakeIcmpProbe { ThrowFor = "8.8.8.8" };
        var runner = new MonitoringCycleRunner(probe, new FixedClock(T0));

        var cycle = await runner.RunAsync(new MonitoringConfiguration(), 1, Context("192.168.1.1"), T0, CancellationToken.None);

        var failed = Assert.Single(cycle.Results, r => r.Target == "8.8.8.8");
        Assert.Equal(ProbeOutcome.Error, failed.Outcome);
        Assert.Equal("UnexpectedProbeError", failed.ErrorCode);
        Assert.Equal(2, cycle.Results.Count(r => r.Succeeded));
    }

    [Fact]
    public async Task Run_Cancellation_Propagates()
    {
        var runner = new MonitoringCycleRunner(new FakeIcmpProbe(), new FixedClock(T0));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunAsync(new MonitoringConfiguration(), 1, Context("192.168.1.1"), T0, cts.Token));
    }

    [Fact]
    public void Schedule_OnTime_AdvancesOneInterval()
    {
        Assert.Equal(new CycleSlot(T0 + Five, 0, false), CycleSchedule.Next(T0, T0.AddSeconds(1.2), Five));
    }

    [Fact]
    public void Schedule_ExactlyAtNextSlot_DoesNotSkip()
    {
        Assert.Equal(new CycleSlot(T0 + Five, 0, false), CycleSchedule.Next(T0, T0 + Five, Five));
    }

    [Fact]
    public void Schedule_Overrun_SkipsMissedSlotsAndStaysAligned()
    {
        var slot = CycleSchedule.Next(T0, T0.AddSeconds(12), Five);

        Assert.Equal(new CycleSlot(T0.AddSeconds(15), 2, false), slot);
    }

    [Fact]
    public void Schedule_LongStall_CountsSkippedSlots()
    {
        var slot = CycleSchedule.Next(T0, T0.AddHours(1), Five);

        Assert.Equal(719, slot.SkippedSlots);
        Assert.Equal(T0.AddHours(1), slot.NextScheduledAtUtc);
    }

    [Fact]
    public void Schedule_ClockMovedBackwards_Resets()
    {
        var now = T0.AddMinutes(-10);

        Assert.Equal(new CycleSlot(now + Five, 0, true), CycleSchedule.Next(T0, now, Five));
    }

    private static NetworkContext Context(string? gateway)
    {
        NetworkInterfaceSnapshot? nic = gateway is null
            ? null
            : new("id", "Wi-Fi", "Test adapter", InterfaceKind.Wifi, InterfaceStatus.Up, 12, ["192.168.1.20"], [], [gateway], [gateway]);
        return new NetworkContext(T0, nic is null ? [] : [nic], nic, gateway);
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;

        public TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class FakeIcmpProbe : IIcmpProbe
    {
        public string? ThrowFor { get; init; }

        public List<(ProbeType Type, string Target)> Calls { get; } = [];

        public Task<ProbeResult> PingAsync(ProbeType probeType, string target, int timeoutMilliseconds, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (Calls)
            {
                Calls.Add((probeType, target));
            }

            if (target == ThrowFor)
            {
                throw new InvalidOperationException("bug");
            }

            return Task.FromResult(new ProbeResult(
                probeType, target, "fake", T0, T0.AddMilliseconds(12), ProbeOutcome.Success, 12, timeoutMilliseconds, null, null));
        }
    }
}
