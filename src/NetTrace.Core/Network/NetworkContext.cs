namespace NetTrace.Core.Network;

/// <summary>Adapter category. Serialized by name; never renumber or rename.</summary>
public enum InterfaceKind
{
    Unknown,
    Ethernet,
    Wifi,
    Cellular,
    Loopback,
    Tunnel,
    Other,
}

/// <summary>Adapter operational status. Serialized by name; never renumber or rename.</summary>
public enum InterfaceStatus
{
    Unknown,
    Up,
    Down,
    Testing,
    Dormant,
    NotPresent,
    LowerLayerDown,
}

/// <summary>A point-in-time view of one network adapter. Addresses are textual IP literals.</summary>
public sealed record NetworkInterfaceSnapshot(
    string Id,
    string Name,
    string Description,
    InterfaceKind Kind,
    InterfaceStatus Status,
    int? Ipv4InterfaceIndex,
    IReadOnlyList<string> Ipv4Addresses,
    IReadOnlyList<string> Ipv6Addresses,
    IReadOnlyList<string> Ipv4Gateways,
    IReadOnlyList<string> DnsServers);

/// <summary>
/// All adapters at capture time plus the one judged to carry internet traffic. A null
/// <see cref="ActiveInterface"/> means no connected adapter has a default gateway.
/// </summary>
public sealed record NetworkContext(
    DateTimeOffset CapturedAtUtc,
    IReadOnlyList<NetworkInterfaceSnapshot> Interfaces,
    NetworkInterfaceSnapshot? ActiveInterface,
    string? DefaultGateway)
{
    public bool HasActiveConnection => ActiveInterface is not null;
}

public interface INetworkInterfaceProvider
{
    /// <summary>Raised (on an arbitrary thread) when Windows reports an address or availability change.</summary>
    event EventHandler? NetworkChanged;

    Task<NetworkContext> GetCurrentAsync(CancellationToken cancellationToken = default);
}
