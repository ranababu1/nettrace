using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using NetTrace.Core.Probes;
using NetTrace.Core.Time;

namespace NetTrace.Infrastructure.Probes;

/// <summary>ICMP echo probe using <see cref="Ping"/>. Each call uses its own <see cref="Ping"/> instance.</summary>
public sealed class PingProbe(IClock clock) : IIcmpProbe
{
    public const string Method = "ICMP echo, 32-byte payload";

    /// <summary>Extra time allowed beyond the ICMP timeout before the probe is abandoned as hung.</summary>
    internal static readonly TimeSpan DeadlineGrace = TimeSpan.FromSeconds(1);

    private static readonly byte[] Payload = new byte[32];

    public async Task<ProbeResult> PingAsync(
        ProbeType probeType, string target, int timeoutMilliseconds, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ArgumentOutOfRangeException.ThrowIfLessThan(timeoutMilliseconds, 1);

        var startedAt = clock.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        if (!IPAddress.TryParse(target, out var address))
        {
            return Result(startedAt, ProbeOutcome.Error, null, "InvalidTarget");
        }

        var timeout = TimeSpan.FromMilliseconds(timeoutMilliseconds);
        try
        {
            using var ping = new Ping();
            var reply = await ping
                .SendPingAsync(address, timeout, Payload, new PingOptions(ttl: 128, dontFragment: false), cancellationToken)
                .WaitAsync(timeout + DeadlineGrace, cancellationToken)
                .ConfigureAwait(false);

            return reply.Status == IPStatus.Success
                ? Result(startedAt, ProbeOutcome.Success, reply.RoundtripTime, null)
                : Result(startedAt, MapStatus(reply.Status), null, reply.Status.ToString());
        }
        catch (TimeoutException)
        {
            return Result(startedAt, ProbeOutcome.Timeout, null, "ProbeDeadlineExceeded");
        }
        catch (PingException ex) when (ex.InnerException is SocketException socket)
        {
            return Result(startedAt, ProbeOutcome.Error, null, "Socket" + socket.SocketErrorCode);
        }
        catch (PingException)
        {
            return Result(startedAt, ProbeOutcome.Error, null, "PingFailed");
        }

        ProbeResult Result(DateTimeOffset started, ProbeOutcome outcome, double? latencyMs, string? errorCode) =>
            new(
                probeType,
                target,
                Method,
                started,
                started + stopwatch.Elapsed,
                outcome,
                latencyMs,
                timeoutMilliseconds,
                errorCode,
                errorCode is null ? null : DescribeError(errorCode));
    }

    internal static ProbeOutcome MapStatus(IPStatus status) => status switch
    {
        IPStatus.Success => ProbeOutcome.Success,
        IPStatus.TimedOut => ProbeOutcome.Timeout,
        IPStatus.DestinationHostUnreachable or IPStatus.DestinationNetworkUnreachable
            or IPStatus.DestinationUnreachable or IPStatus.DestinationProtocolUnreachable
            or IPStatus.DestinationPortUnreachable or IPStatus.TtlExpired or IPStatus.TimeExceeded
            or IPStatus.TtlReassemblyTimeExceeded or IPStatus.BadRoute or IPStatus.NoResources
            or IPStatus.SourceQuench or IPStatus.PacketTooBig or IPStatus.ParameterProblem => ProbeOutcome.Failure,
        _ => ProbeOutcome.Error,
    };

    internal static string DescribeError(string errorCode) => errorCode switch
    {
        nameof(IPStatus.TimedOut) => "No reply was received before the timeout.",
        "ProbeDeadlineExceeded" => "The probe did not complete within its timeout and was abandoned.",
        nameof(IPStatus.DestinationHostUnreachable) => "The destination host was reported unreachable.",
        nameof(IPStatus.DestinationNetworkUnreachable) => "The destination network was reported unreachable.",
        nameof(IPStatus.TtlExpired) or nameof(IPStatus.TimeExceeded) => "The packet expired in transit.",
        "InvalidTarget" => "The target is not a valid IP address.",
        _ when errorCode.StartsWith("Socket", StringComparison.Ordinal) => "The operating system could not send the probe.",
        _ => "The probe failed.",
    };
}
