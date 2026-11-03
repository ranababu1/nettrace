using NetTrace.Core.Network;
using NetTrace.Core.Throughput;

namespace NetTrace.Core.Tests;

public class ThroughputTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0d, "0k")]
    [InlineData(0.5d, "1k")]
    [InlineData(999d, "1k")]
    [InlineData(1_000d, "1k")]
    [InlineData(1_999d, "1k")]
    [InlineData(310_400d, "310k")]
    [InlineData(999_999d, "999k")]
    [InlineData(1_000_000d, "1m")]
    [InlineData(4_999_999d, "4m")]
    [InlineData(125_000_000d, "125m")]
    public void Format_BytesFollowsPlanRules(double bytesPerSecond, string expected)
    {
        Assert.Equal(expected, RateFormatter.Format(bytesPerSecond, RateUnit.BytesPerSecond));
    }

    [Theory]
    [InlineData(125_000d, "1m")]   // 1,000,000 bits/s
    [InlineData(124_999d, "999k")]
    [InlineData(100d, "1k")]       // 800 bits/s
    public void Format_BitsUsesSameRules(double bytesPerSecond, string expected)
    {
        Assert.Equal(expected, RateFormatter.Format(bytesPerSecond, RateUnit.BitsPerSecond));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(double.NaN)]
    [InlineData(-5d)]
    public void Format_UnavailableIsNeverZero(double? value)
    {
        Assert.Equal(RateFormatter.UnavailableText, RateFormatter.Format(value, RateUnit.BytesPerSecond));
    }

    [Fact]
    public void Rate_IsDeltaOverElapsedSeconds()
    {
        var previous = new CounterReading("eth", T0, 1_000, 500);
        var current = new CounterReading("eth", T0.AddSeconds(2), 9_000, 1_500);

        Assert.Equal((4_000d, 500d), ThroughputCalculator.Rate(previous, current));
    }

    [Fact]
    public void Rate_FirstSampleIsUnavailable()
    {
        Assert.Equal((null, null), ThroughputCalculator.Rate(null, new CounterReading("eth", T0, 1, 1)));
    }

    [Fact]
    public void Rate_CounterResetOrWrapIsUnavailableNotNegativeOrSpike()
    {
        var previous = new CounterReading("eth", T0, 9_000_000, 9_000);
        var current = new CounterReading("eth", T0.AddSeconds(1), 100, 50);

        Assert.Equal((null, null), ThroughputCalculator.Rate(previous, current));
    }

    [Fact]
    public void Rate_AdapterSwitchIsUnavailable()
    {
        var previous = new CounterReading("eth", T0, 1_000, 1_000);
        var current = new CounterReading("wifi", T0.AddSeconds(1), 50_000_000, 1_000);

        Assert.Equal((null, null), ThroughputCalculator.Rate(previous, current));
    }

    [Fact]
    public void Rate_NonPositiveElapsedIsUnavailable()
    {
        var previous = new CounterReading("eth", T0, 1_000, 1_000);

        Assert.Equal((null, null), ThroughputCalculator.Rate(previous, previous with { BytesReceived = 2_000 }));
    }

    [Fact]
    public void Select_AutomaticFollowsActiveConnection()
    {
        var wifi = Nic("wifi", InterfaceKind.Wifi);
        var context = new NetworkContext(T0, [Nic("eth", InterfaceKind.Ethernet), wifi], wifi, "192.168.1.1");

        Assert.Equal((wifi, null), ThroughputInterfaceSelector.Select(context, selectedInterfaceId: null));
    }

    [Fact]
    public void Select_AutomaticWithoutConnectionIsUnavailable()
    {
        var context = new NetworkContext(T0, [], null, null);

        Assert.Equal((null, "No active connection"), ThroughputInterfaceSelector.Select(context, null));
    }

    [Fact]
    public void Select_SpecificAdapterIsUsedEvenIfNotActive()
    {
        var eth = Nic("eth", InterfaceKind.Ethernet);
        var wifi = Nic("wifi", InterfaceKind.Wifi);
        var context = new NetworkContext(T0, [eth, wifi], wifi, "192.168.1.1");

        Assert.Equal((eth, null), ThroughputInterfaceSelector.Select(context, "eth"));
    }

    [Fact]
    public void Select_DisconnectedSpecificAdapterIsUnavailableAndNotReplaced()
    {
        var eth = Nic("eth", InterfaceKind.Ethernet, InterfaceStatus.Down);
        var wifi = Nic("wifi", InterfaceKind.Wifi);
        var context = new NetworkContext(T0, [eth, wifi], wifi, "192.168.1.1");

        Assert.Equal((eth, "Selected connection is disconnected"), ThroughputInterfaceSelector.Select(context, "eth"));
    }

    [Fact]
    public void Select_RemovedSpecificAdapterIsUnavailable()
    {
        var wifi = Nic("wifi", InterfaceKind.Wifi);
        var context = new NetworkContext(T0, [wifi], wifi, "192.168.1.1");

        Assert.Equal((null, "Selected connection not found"), ThroughputInterfaceSelector.Select(context, "usb-eth"));
    }

    [Fact]
    public void Selectable_ExcludesLoopback()
    {
        var context = new NetworkContext(T0, [Nic("lo", InterfaceKind.Loopback), Nic("eth", InterfaceKind.Ethernet)], null, null);

        Assert.Equal(["eth"], ThroughputInterfaceSelector.Selectable(context).Select(i => i.Id));
    }

    private static NetworkInterfaceSnapshot Nic(string id, InterfaceKind kind, InterfaceStatus status = InterfaceStatus.Up) =>
        new(id, id, id, kind, status, 1, [], [], ["192.168.1.1"], []);
}
