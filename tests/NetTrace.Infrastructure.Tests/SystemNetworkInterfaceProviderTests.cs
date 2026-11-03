using System.Net.NetworkInformation;
using Microsoft.Extensions.Logging.Abstractions;
using NetTrace.Core.Network;
using NetTrace.Infrastructure.Network;

namespace NetTrace.Infrastructure.Tests;

public class SystemNetworkInterfaceProviderTests
{
    [Theory]
    [InlineData(NetworkInterfaceType.Ethernet, InterfaceKind.Ethernet)]
    [InlineData(NetworkInterfaceType.GigabitEthernet, InterfaceKind.Ethernet)]
    [InlineData(NetworkInterfaceType.Wireless80211, InterfaceKind.Wifi)]
    [InlineData(NetworkInterfaceType.Wwanpp2, InterfaceKind.Cellular)]
    [InlineData(NetworkInterfaceType.Loopback, InterfaceKind.Loopback)]
    [InlineData(NetworkInterfaceType.Tunnel, InterfaceKind.Tunnel)]
    [InlineData(NetworkInterfaceType.Ppp, InterfaceKind.Other)]
    [InlineData(NetworkInterfaceType.Unknown, InterfaceKind.Unknown)]
    public void MapKind_TranslatesAdapterTypes(NetworkInterfaceType type, InterfaceKind expected)
    {
        Assert.Equal(expected, SystemNetworkInterfaceProvider.MapKind(type));
    }

    [Theory]
    [InlineData(OperationalStatus.Up, InterfaceStatus.Up)]
    [InlineData(OperationalStatus.Down, InterfaceStatus.Down)]
    [InlineData(OperationalStatus.LowerLayerDown, InterfaceStatus.LowerLayerDown)]
    [InlineData(OperationalStatus.Unknown, InterfaceStatus.Unknown)]
    public void MapStatus_TranslatesOperationalStatus(OperationalStatus status, InterfaceStatus expected)
    {
        Assert.Equal(expected, SystemNetworkInterfaceProvider.MapStatus(status));
    }

    // Reads local adapter state only; needs no internet access and sends no traffic.
    [Fact]
    public async Task GetCurrentAsync_DiscoversLocalAdaptersConsistently()
    {
        var now = new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
        using var provider = new SystemNetworkInterfaceProvider(new FakeClock(now), NullLogger<SystemNetworkInterfaceProvider>.Instance);

        var context = await provider.GetCurrentAsync();

        Assert.Equal(now, context.CapturedAtUtc);
        Assert.Contains(context.Interfaces, i => i.Kind == InterfaceKind.Loopback);
        if (context.ActiveInterface is { } active)
        {
            Assert.True(ActiveInterfaceSelector.IsEligible(active));
            Assert.Equal(ActiveInterfaceSelector.DefaultGatewayOf(active), context.DefaultGateway);
        }
        else
        {
            Assert.Null(context.DefaultGateway);
        }
    }
}
