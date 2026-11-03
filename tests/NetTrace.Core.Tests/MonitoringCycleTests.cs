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
    public async Task Run_SlowProbes_RunOnFirstCycleThenOnlyWhenTheirIntervalElapses()
    {
        var dns = new FakeDnsProbe();
        var https = new FakeHttpsProbe();
        var runner = new MonitoringCycleRunner(new FakeIcmpProbe(), new FixedClock(T0), dns, https);
        var config = new MonitoringConfiguration();
        var context = Context("192.168.1.1");

        var first = await runner.RunAsync(config, 1, context, T0, CancellationToken.None);
        var second = await runner.RunAsync(config, 1, context, T0.AddSeconds(25), CancellationToken.None);
        var third = await runner.RunAsync(config, 1, context, T0.AddSeconds(30), CancellationToken.None);

        Assert.Equal(1, first.Results.Count(r => r.ProbeType == ProbeType.Dns));
        Assert.Equal(2, first.Results.Count(r => r.ProbeType == ProbeType.Https));
        Assert.DoesNotContain(second.Results, r => r.ProbeType is ProbeType.Dns or ProbeType.Https);
        Assert.Equal(1, third.Results.Count(r => r.ProbeType == ProbeType.Dns));
        Assert.Equal(2, third.Results.Count(r => r.ProbeType == ProbeType.Https));
        Assert.Equal(2, dns.Calls);
    }

    [Fact]
    public async Task Run_SlowProbeStillRunning_DoesNotDelayCycle_AndIsReportedWhenFinished()
    {
        var gate = new TaskCompletionSource();
        var dns = new FakeDnsProbe { Gate = gate.Task };
        var runner = new MonitoringCycleRunner(new FakeIcmpProbe(), new FixedClock(T0), dns);
        var config = new MonitoringConfiguration();
        var context = Context("192.168.1.1");

        var first = await runner.RunAsync(config, 1, context, T0, CancellationToken.None);
        gate.SetResult();
        await Task.Delay(100);
        var second = await runner.RunAsync(config, 1, context, T0.AddSeconds(5), CancellationToken.None);

        Assert.DoesNotContain(first.Results, r => r.ProbeType == ProbeType.Dns);
        Assert.Single(second.Results, r => r.ProbeType == ProbeType.Dns);
        Assert.Equal(1, dns.Calls);
    }

    [Fact]
    public async Task Run_SlowProbeThrowing_IsRecordedAsErrorWithItsOwnMethod()
    {
        var runner = new MonitoringCycleRunner(new FakeIcmpProbe(), new FixedClock(T0), new FakeDnsProbe { Throw = true });

        var cycle = await runner.RunAsync(new MonitoringConfiguration(), 1, Context("192.168.1.1"), T0, CancellationToken.None);

        var dns = Assert.Single(cycle.Results, r => r.ProbeType == ProbeType.Dns);
        Assert.Equal(ProbeOutcome.Error, dns.Outcome);
        Assert.Equal("System resolver", dns.Method);
        Assert.Equal(3, cycle.Results.Count(r => r.Succeeded));
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

    private sealed class FakeDnsProbe : IDnsProbe
    {
        public int Calls { get; private set; }

        public bool Throw { get; init; }

        public Task? Gate { get; init; }

        public async Task<ProbeResult> ResolveAsync(string hostname, int timeoutMilliseconds, CancellationToken cancellationToken)
        {
            if (Gate is not null)
            {
                await Gate;
            }

            Calls++;
            if (Throw)
            {
                throw new InvalidOperationException("boom");
            }

            return new ProbeResult(ProbeType.Dns, hostname, "System resolver", T0, T0, ProbeOutcome.Success, 10, timeoutMilliseconds, null, null);
        }
    }

    private sealed class FakeHttpsProbe : IHttpsProbe
    {
        public Task<ProbeResult> GetAsync(string endpoint, int timeoutMilliseconds, CancellationToken cancellationToken) =>
            Task.FromResult(new ProbeResult(ProbeType.Https, endpoint, "HTTPS GET", T0, T0, ProbeOutcome.Success, 50, timeoutMilliseconds, null, null));
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
