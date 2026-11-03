using NetTrace.Core.Network;

namespace NetTrace.Core.Tests;

public class ActiveInterfaceSelectorTests
{
    [Fact]
    public void RoutedInterface_WinsWhenEligible()
    {
        var ethernet = Nic("eth", InterfaceKind.Ethernet, index: 4);
        var wifi = Nic("wifi", InterfaceKind.Wifi, index: 12);

        Assert.Same(wifi, ActiveInterfaceSelector.Select([ethernet, wifi], routedInterfaceIndex: 12));
    }

    [Fact]
    public void RoutedInterfaceNotEligible_FallsBackToPreferenceOrder()
    {
        var vpn = Nic("vpn", InterfaceKind.Tunnel, index: 30);
        var wifi = Nic("wifi", InterfaceKind.Wifi, index: 12);
        var ethernet = Nic("eth", InterfaceKind.Ethernet, index: 4);

        Assert.Same(ethernet, ActiveInterfaceSelector.Select([vpn, wifi, ethernet], routedInterfaceIndex: 30));
    }

    [Fact]
    public void ExcludesLoopbackDownAndGatewaylessAdapters()
    {
        NetworkInterfaceSnapshot[] interfaces =
        [
            Nic("lo", InterfaceKind.Loopback, index: 1),
            Nic("down", InterfaceKind.Ethernet, index: 2, status: InterfaceStatus.Down),
            Nic("hyperv", InterfaceKind.Ethernet, index: 3, gateways: []),
            Nic("unset", InterfaceKind.Ethernet, index: 5, gateways: ["0.0.0.0"]),
        ];

        Assert.Null(ActiveInterfaceSelector.Select(interfaces, routedInterfaceIndex: null));
    }

    [Fact]
    public void NoInterfaces_ReturnsNull()
    {
        Assert.Null(ActiveInterfaceSelector.Select([], routedInterfaceIndex: 7));
    }

    [Fact]
    public void DefaultGateway_SkipsUnspecifiedAddress()
    {
        var nic = Nic("eth", InterfaceKind.Ethernet, index: 4, gateways: ["0.0.0.0", "192.168.1.1"]);

        Assert.Equal("192.168.1.1", ActiveInterfaceSelector.DefaultGatewayOf(nic));
        Assert.Null(ActiveInterfaceSelector.DefaultGatewayOf(null));
    }

    [Fact]
    public void Fallback_IsDeterministicByName()
    {
        var b = Nic("b", InterfaceKind.Wifi, index: 8, name: "Wi-Fi 2");
        var a = Nic("a", InterfaceKind.Wifi, index: 9, name: "Wi-Fi");

        Assert.Same(a, ActiveInterfaceSelector.Select([b, a], routedInterfaceIndex: null));
    }

    private static NetworkInterfaceSnapshot Nic(
        string id,
        InterfaceKind kind,
        int index,
        InterfaceStatus status = InterfaceStatus.Up,
        IReadOnlyList<string>? gateways = null,
        string? name = null) =>
        new(id, name ?? id, id, kind, status, index, ["192.168.1.20"], [], gateways ?? ["192.168.1.1"], ["192.168.1.1"]);
}
