using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NetTrace.Core.Network;
using NetTrace.Core.Time;

namespace NetTrace.Infrastructure.Network;

/// <summary>
/// Discovers adapters through <see cref="System.Net.NetworkInformation"/> and asks the Windows
/// routing table which interface carries internet-bound traffic. Discovery sends no traffic.
/// </summary>
public sealed class SystemNetworkInterfaceProvider : INetworkInterfaceProvider, IDisposable
{
    // Any public address works: GetBestInterface only consults the routing table.
    private static readonly IPAddress RoutingLookupAddress = IPAddress.Parse("1.1.1.1");

    private readonly IClock _clock;
    private readonly ILogger<SystemNetworkInterfaceProvider> _logger;
    private bool _disposed;

    public SystemNetworkInterfaceProvider(IClock clock, ILogger<SystemNetworkInterfaceProvider> logger)
    {
        _clock = clock;
        _logger = logger;
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
    }

    public event EventHandler? NetworkChanged;

    public Task<NetworkContext> GetCurrentAsync(CancellationToken cancellationToken = default) =>
        Task.Run(Capture, cancellationToken);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
    }

    internal static InterfaceKind MapKind(NetworkInterfaceType type) => type switch
    {
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.Ethernet3Megabit or NetworkInterfaceType.FastEthernetT
            or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.GigabitEthernet => InterfaceKind.Ethernet,
        NetworkInterfaceType.Wireless80211 => InterfaceKind.Wifi,
        NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 => InterfaceKind.Cellular,
        NetworkInterfaceType.Loopback => InterfaceKind.Loopback,
        NetworkInterfaceType.Tunnel => InterfaceKind.Tunnel,
        NetworkInterfaceType.Unknown => InterfaceKind.Unknown,
        _ => InterfaceKind.Other,
    };

    internal static InterfaceStatus MapStatus(OperationalStatus status) => status switch
    {
        OperationalStatus.Up => InterfaceStatus.Up,
        OperationalStatus.Down => InterfaceStatus.Down,
        OperationalStatus.Testing => InterfaceStatus.Testing,
        OperationalStatus.Dormant => InterfaceStatus.Dormant,
        OperationalStatus.NotPresent => InterfaceStatus.NotPresent,
        OperationalStatus.LowerLayerDown => InterfaceStatus.LowerLayerDown,
        _ => InterfaceStatus.Unknown,
    };

    private NetworkContext Capture()
    {
        var capturedAt = _clock.UtcNow;
        var snapshots = new List<NetworkInterfaceSnapshot>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                snapshots.Add(ToSnapshot(nic));
            }
            catch (NetworkInformationException ex)
            {
                // One misbehaving adapter must not hide the others.
                _logger.LogWarning(ex, "Could not read network adapter {AdapterName}.", nic.Name);
            }
        }

        var routedIndex = TryGetRoutedInterfaceIndex();
        var active = ActiveInterfaceSelector.Select(snapshots, routedIndex);
        return new NetworkContext(capturedAt, snapshots, active, ActiveInterfaceSelector.DefaultGatewayOf(active));
    }

    private static NetworkInterfaceSnapshot ToSnapshot(NetworkInterface nic)
    {
        var properties = nic.GetIPProperties();
        return new NetworkInterfaceSnapshot(
            nic.Id,
            nic.Name,
            nic.Description,
            MapKind(nic.NetworkInterfaceType),
            MapStatus(nic.OperationalStatus),
            TryGetIpv4Index(properties),
            AddressesOf(properties.UnicastAddresses.Select(a => a.Address), AddressFamily.InterNetwork),
            AddressesOf(properties.UnicastAddresses.Select(a => a.Address), AddressFamily.InterNetworkV6),
            AddressesOf(properties.GatewayAddresses.Select(g => g.Address), AddressFamily.InterNetwork),
            properties.DnsAddresses.Select(a => a.ToString()).Distinct(StringComparer.Ordinal).ToList());
    }

    private static List<string> AddressesOf(IEnumerable<IPAddress> addresses, AddressFamily family) =>
        addresses.Where(a => a.AddressFamily == family).Select(a => a.ToString()).Distinct(StringComparer.Ordinal).ToList();

    private static int? TryGetIpv4Index(IPInterfaceProperties properties)
    {
        try
        {
            return properties.GetIPv4Properties()?.Index;
        }
        catch (NetworkInformationException)
        {
            // IPv4 is not enabled on this adapter.
            return null;
        }
    }

    private int? TryGetRoutedInterfaceIndex()
    {
        var destination = BitConverter.ToUInt32(RoutingLookupAddress.GetAddressBytes(), 0);
        var result = NativeMethods.GetBestInterface(destination, out var index);
        if (result == 0)
        {
            return checked((int)index);
        }

        // ERROR_NETWORK_UNREACHABLE and similar: no route, so fall back to adapter preference.
        _logger.LogDebug("GetBestInterface returned {Win32Error}; no default route is available.", result);
        return null;
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e) => NetworkChanged?.Invoke(this, EventArgs.Empty);

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) =>
        NetworkChanged?.Invoke(this, EventArgs.Empty);

    private static class NativeMethods
    {
        /// <param name="destinationAddress">IPv4 address in network byte order.</param>
        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern uint GetBestInterface(uint destinationAddress, out uint bestInterfaceIndex);
    }
}
