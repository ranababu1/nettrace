using System.Globalization;
using NetTrace.Core.Network;

namespace NetTrace.Core.Throughput;

/// <summary>Display unit for live throughput. Serialized by name.</summary>
public enum RateUnit
{
    BytesPerSecond,
    BitsPerSecond,
}

/// <summary>Live throughput display settings (plan.md section 4.11). Display-only: never persisted per sample.</summary>
public sealed record ThroughputConfiguration
{
    public bool Enabled { get; init; } = true;

    /// <summary>Adapter id to track, or null for Automatic (the adapter carrying the default route).</summary>
    public string? SelectedInterfaceId { get; init; }

    public bool ShowRateInTrayIcon { get; init; } = true;

    public bool ShowConnectionLabel { get; init; } = true;

    public int SampleIntervalSeconds { get; init; } = 1;

    public RateUnit Unit { get; init; } = RateUnit.BytesPerSecond;
}

/// <summary>Cumulative OS byte counters for one adapter at one instant.</summary>
public sealed record CounterReading(string InterfaceId, DateTimeOffset AtUtc, long BytesReceived, long BytesSent);

/// <summary>
/// One throughput sample. Rates are null when unavailable (first sample, adapter missing or
/// disconnected, counter reset); they are never reported as zero in that case.
/// </summary>
public sealed record ThroughputSample(
    DateTimeOffset AtUtc,
    string? InterfaceId,
    string? InterfaceName,
    InterfaceKind? InterfaceKind,
    double? DownloadBytesPerSecond,
    double? UploadBytesPerSecond,
    string? UnavailableReason)
{
    public bool IsAvailable => DownloadBytesPerSecond is not null && UploadBytesPerSecond is not null;

    public static ThroughputSample Unavailable(DateTimeOffset atUtc, NetworkInterfaceSnapshot? nic, string reason) =>
        new(atUtc, nic?.Id, nic?.Name, nic?.Kind, null, null, reason);
}

public static class ThroughputCalculator
{
    /// <summary>
    /// Rate between two readings of the same adapter, or nulls when no honest rate exists:
    /// no previous reading, a different adapter, non-positive elapsed time, or a counter that
    /// went backwards (reset or wrap). This avoids spikes and negative values.
    /// </summary>
    public static (double? Download, double? Upload) Rate(CounterReading? previous, CounterReading current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (previous is null || previous.InterfaceId != current.InterfaceId)
        {
            return (null, null);
        }

        var seconds = (current.AtUtc - previous.AtUtc).TotalSeconds;
        var received = current.BytesReceived - previous.BytesReceived;
        var sent = current.BytesSent - previous.BytesSent;
        if (seconds <= 0 || received < 0 || sent < 0)
        {
            return (null, null);
        }

        return (received / seconds, sent / seconds);
    }
}

/// <summary>
/// Compact rate text per plan.md section 4.11: whole numbers rounded down, lowercase <c>m</c>/<c>k</c>
/// suffixes (decimal units: 1k = 1,000, 1m = 1,000,000), <c>k</c> as the smallest unit, any non-zero
/// rate below 1k shown as <c>1k</c>, and exactly zero as <c>0k</c>.
/// </summary>
public static class RateFormatter
{
    public const string UnavailableText = "--";

    public static string Format(double? bytesPerSecond, RateUnit unit)
    {
        if (bytesPerSecond is not { } bytes || double.IsNaN(bytes) || bytes < 0)
        {
            return UnavailableText;
        }

        var value = unit == RateUnit.BitsPerSecond ? bytes * 8 : bytes;
        if (value == 0)
        {
            return "0k";
        }

        if (value >= 1_000_000)
        {
            return Math.Floor(value / 1_000_000).ToString("0", CultureInfo.InvariantCulture) + "m";
        }

        return value >= 1_000
            ? Math.Floor(value / 1_000).ToString("0", CultureInfo.InvariantCulture) + "k"
            : "1k";
    }

    public static string DescribeKind(InterfaceKind? kind) => kind switch
    {
        InterfaceKind.Wifi => "Wi-Fi",
        InterfaceKind.Ethernet => "Ethernet",
        InterfaceKind.Cellular => "Cellular",
        null => "No connection",
        _ => "Other",
    };
}

public static class ThroughputInterfaceSelector
{
    /// <summary>
    /// Automatic follows the active connection. A specific selection is never silently replaced:
    /// if that adapter is missing or disconnected the result is unavailable, and tracking
    /// resumes when it returns.
    /// </summary>
    public static (NetworkInterfaceSnapshot? Interface, string? UnavailableReason) Select(
        NetworkContext context, string? selectedInterfaceId)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (selectedInterfaceId is null)
        {
            return context.ActiveInterface is { } active
                ? (active, null)
                : (null, "No active connection");
        }

        var selected = context.Interfaces.FirstOrDefault(i => i.Id == selectedInterfaceId);
        if (selected is null)
        {
            return (null, "Selected connection not found");
        }

        if (selected.Kind == InterfaceKind.Loopback)
        {
            return (null, "Loopback cannot be tracked");
        }

        return selected.Status == InterfaceStatus.Up
            ? (selected, null)
            : (selected, "Selected connection is disconnected");
    }

    /// <summary>Adapters a user may pick from: everything except loopback.</summary>
    public static IReadOnlyList<NetworkInterfaceSnapshot> Selectable(NetworkContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Interfaces.Where(i => i.Kind != InterfaceKind.Loopback).ToList();
    }
}

public interface IThroughputSource
{
    ThroughputSample? Latest { get; }

    ThroughputConfiguration Configuration { get; }

    /// <summary>Raised on a background thread at most once per sample interval.</summary>
    event EventHandler<ThroughputSample>? SampleAvailable;
}

public interface IInterfaceCounterReader
{
    /// <summary>Reads the adapter's cumulative byte counters, or null if the adapter or its counters are unavailable.</summary>
    CounterReading? Read(string interfaceId, DateTimeOffset atUtc);
}
