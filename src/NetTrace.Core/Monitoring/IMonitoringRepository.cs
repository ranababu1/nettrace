using NetTrace.Core.Network;
using NetTrace.Core.Probes;

namespace NetTrace.Core.Monitoring;

/// <summary>A persisted probe result with the cycle context it was recorded in.</summary>
public sealed record StoredObservation(
    long Id,
    Guid CycleId,
    int ConfigurationVersion,
    string? InterfaceId,
    ProbeResult Result);

public interface IObservationRepository
{
    /// <summary>
    /// Persists every result of <paramref name="cycle"/> atomically. When
    /// <paramref name="recordNetworkSnapshot"/> is true the cycle's network context is stored too.
    /// </summary>
    Task SaveCycleAsync(MonitoringCycle cycle, bool recordNetworkSnapshot, CancellationToken cancellationToken = default);

    /// <summary>Observations whose start time is in [<paramref name="fromUtc"/>, <paramref name="toUtc"/>), oldest first.</summary>
    Task<IReadOnlyList<StoredObservation>> GetObservationsAsync(
        DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken = default);
}

public interface IMonitoringGapRepository
{
    /// <summary>Stores a gap and returns its id. Open gaps have a null end.</summary>
    Task<long> AddAsync(MonitoringGap gap, CancellationToken cancellationToken = default);

    Task CloseAsync(long gapId, DateTimeOffset endedAtUtc, CancellationToken cancellationToken = default);

    /// <summary>Gaps overlapping [<paramref name="fromUtc"/>, <paramref name="toUtc"/>); open gaps overlap everything after their start.</summary>
    Task<IReadOnlyList<MonitoringGap>> GetOverlappingAsync(
        DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken = default);
}

/// <summary>Decides when the network context changed enough to record a new snapshot.</summary>
public static class NetworkContextChange
{
    public static bool IsMaterial(NetworkContext? previous, NetworkContext current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (previous is null)
        {
            return true;
        }

        var a = previous.ActiveInterface;
        var b = current.ActiveInterface;
        if (a is null || b is null)
        {
            return a is not null || b is not null;
        }

        return a.Id != b.Id
            || a.Status != b.Status
            || a.Kind != b.Kind
            || previous.DefaultGateway != current.DefaultGateway
            || !a.Ipv4Addresses.SequenceEqual(b.Ipv4Addresses)
            || !a.DnsServers.SequenceEqual(b.DnsServers);
    }
}
