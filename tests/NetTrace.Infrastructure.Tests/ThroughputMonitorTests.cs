using Microsoft.Extensions.Logging.Abstractions;
using NetTrace.Core.Network;
using NetTrace.Core.Throughput;
using NetTrace.Infrastructure.Throughput;

namespace NetTrace.Infrastructure.Tests;

public class ThroughputMonitorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TakeSample_FirstReadingIsMeasuringThenRate()
    {
        var counters = new FakeCounters();
        var monitor = Create(counters);
        var context = Context(Nic("eth", InterfaceKind.Ethernet));

        counters.Values["eth"] = (1_000, 100);
        var (first, reading) = monitor.TakeSample(context, null, T0);
        counters.Values["eth"] = (4_001_000, 310_100);
        var (second, _) = monitor.TakeSample(context, reading, T0.AddSeconds(1));

        Assert.False(first.IsAvailable);
        Assert.Equal("Measuring", first.UnavailableReason);
        Assert.Equal(4_000_000, second.DownloadBytesPerSecond);
        Assert.Equal(310_000, second.UploadBytesPerSecond);
        Assert.Equal(InterfaceKind.Ethernet, second.InterfaceKind);
    }

    [Fact]
    public void TakeSample_CounterResetShowsUnavailableThenRecovers()
    {
        var counters = new FakeCounters();
        var monitor = Create(counters);
        var context = Context(Nic("eth", InterfaceKind.Ethernet));

        counters.Values["eth"] = (9_000_000, 9_000);
        var (_, r1) = monitor.TakeSample(context, null, T0);
        counters.Values["eth"] = (10, 10);
        var (reset, r2) = monitor.TakeSample(context, r1, T0.AddSeconds(1));
        counters.Values["eth"] = (2_010, 1_010);
        var (recovered, _) = monitor.TakeSample(context, r2, T0.AddSeconds(2));

        Assert.Equal("Counter reset", reset.UnavailableReason);
        Assert.Null(reset.DownloadBytesPerSecond);
        Assert.Equal(2_000, recovered.DownloadBytesPerSecond);
    }

    [Fact]
    public void TakeSample_NoConnectionIsUnavailable()
    {
        var monitor = Create(new FakeCounters());

        var (sample, reading) = monitor.TakeSample(new NetworkContext(T0, [], null, null), null, T0);

        Assert.Equal("No active connection", sample.UnavailableReason);
        Assert.Null(reading);
    }

    [Fact]
    public void TakeSample_MissingCountersIsUnavailable()
    {
        var monitor = Create(new FakeCounters());

        var (sample, _) = monitor.TakeSample(Context(Nic("eth", InterfaceKind.Ethernet)), null, T0);

        Assert.Equal("Counters unavailable", sample.UnavailableReason);
    }

    [Fact]
    public void TakeSample_SpecificDisconnectedAdapterIsUnavailableAndNotSwitched()
    {
        var counters = new FakeCounters();
        counters.Values["wifi"] = (1, 1);
        var monitor = Create(counters, new ThroughputConfiguration { SelectedInterfaceId = "eth" });
        var eth = Nic("eth", InterfaceKind.Ethernet, InterfaceStatus.Down);
        var wifi = Nic("wifi", InterfaceKind.Wifi);

        var (sample, _) = monitor.TakeSample(new NetworkContext(T0, [eth, wifi], wifi, "192.168.1.1"), null, T0);

        Assert.Equal("eth", sample.InterfaceId);
        Assert.Equal("Selected connection is disconnected", sample.UnavailableReason);
    }

    // Queries real adapters only; sends no traffic and needs no internet.
    [Fact]
    public void SystemCounterReader_UnknownAdapterReturnsNull()
    {
        var reader = new SystemInterfaceCounterReader();

        Assert.Null(reader.Read("no-such-adapter", T0));
    }

    private static ThroughputMonitor Create(IInterfaceCounterReader counters, ThroughputConfiguration? configuration = null) =>
        new(new NullNetwork(), counters, new FakeClock(T0), NullLogger<ThroughputMonitor>.Instance,
            configuration ?? new ThroughputConfiguration(), (_, _) => Task.CompletedTask);

    private static NetworkInterfaceSnapshot Nic(string id, InterfaceKind kind, InterfaceStatus status = InterfaceStatus.Up) =>
        new(id, id, id, kind, status, 1, [], [], ["192.168.1.1"], []);

    private static NetworkContext Context(NetworkInterfaceSnapshot active) => new(T0, [active], active, "192.168.1.1");

    private sealed class FakeCounters : IInterfaceCounterReader
    {
        public Dictionary<string, (long Received, long Sent)> Values { get; } = [];

        public CounterReading? Read(string interfaceId, DateTimeOffset atUtc) =>
            Values.TryGetValue(interfaceId, out var v) ? new CounterReading(interfaceId, atUtc, v.Received, v.Sent) : null;
    }

    private sealed class NullNetwork : INetworkInterfaceProvider
    {
        public event EventHandler? NetworkChanged { add { } remove { } }

        public Task<NetworkContext> GetCurrentAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new NetworkContext(T0, [], null, null));
    }
}
