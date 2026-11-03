namespace NetTrace.Core.Network;

/// <summary>
/// Chooses the adapter that carries the default route. The OS routing decision
/// (<c>routedInterfaceIndex</c>) wins when it names an eligible adapter; otherwise a
/// deterministic preference order is used.
/// </summary>
public static class ActiveInterfaceSelector
{
    public static NetworkInterfaceSnapshot? Select(
        IEnumerable<NetworkInterfaceSnapshot> interfaces, int? routedInterfaceIndex)
    {
        ArgumentNullException.ThrowIfNull(interfaces);
        var candidates = interfaces.Where(IsEligible).ToList();

        if (routedInterfaceIndex is { } index
            && candidates.FirstOrDefault(i => i.Ipv4InterfaceIndex == index) is { } routed)
        {
            return routed;
        }

        return candidates
            .OrderBy(i => KindPreference(i.Kind))
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.Id, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    public static string? DefaultGatewayOf(NetworkInterfaceSnapshot? snapshot) =>
        snapshot?.Ipv4Gateways.FirstOrDefault(IsUsableGateway);

    public static bool IsEligible(NetworkInterfaceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.Status == InterfaceStatus.Up
            && snapshot.Kind is not (InterfaceKind.Loopback or InterfaceKind.Tunnel)
            && snapshot.Ipv4Gateways.Any(IsUsableGateway);
    }

    private static bool IsUsableGateway(string gateway) =>
        !string.IsNullOrWhiteSpace(gateway) && gateway != "0.0.0.0";

    private static int KindPreference(InterfaceKind kind) => kind switch
    {
        InterfaceKind.Ethernet => 0,
        InterfaceKind.Wifi => 1,
        InterfaceKind.Cellular => 2,
        _ => 3,
    };
}
