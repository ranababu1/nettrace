using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using NetTrace.Core.Probes;
using NetTrace.Core.Time;

namespace NetTrace.Infrastructure.Probes;

/// <summary>DNS resolution probe using the operating system resolver (its cache may answer).</summary>
public sealed class DnsProbe(IClock clock) : IDnsProbe
{
    public const string Method = "System resolver";

    public async Task<ProbeResult> ResolveAsync(string hostname, int timeoutMilliseconds, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostname);
        ArgumentOutOfRangeException.ThrowIfLessThan(timeoutMilliseconds, 1);

        var startedAt = clock.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(hostname, cancellationToken)
                .WaitAsync(TimeSpan.FromMilliseconds(timeoutMilliseconds), cancellationToken)
                .ConfigureAwait(false);
            return addresses.Length > 0
                ? Result(ProbeOutcome.Success, stopwatch.Elapsed.TotalMilliseconds, null)
                : Result(ProbeOutcome.Failure, null, "NoAddresses");
        }
        catch (TimeoutException)
        {
            return Result(ProbeOutcome.Timeout, null, "ResolutionTimedOut");
        }
        catch (SocketException ex)
        {
            return Result(ProbeOutcome.Failure, null, "Socket" + ex.SocketErrorCode);
        }
        catch (ArgumentException)
        {
            return Result(ProbeOutcome.Error, null, "InvalidTarget");
        }

        ProbeResult Result(ProbeOutcome outcome, double? latencyMs, string? errorCode) =>
            new(ProbeType.Dns, hostname, Method, startedAt, startedAt + stopwatch.Elapsed, outcome, latencyMs,
                timeoutMilliseconds, errorCode, errorCode is null ? null : Describe(errorCode));
    }

    internal static string Describe(string errorCode) => errorCode switch
    {
        "ResolutionTimedOut" => "The name was not resolved before the timeout.",
        "NoAddresses" => "The name resolved to no addresses.",
        "InvalidTarget" => "The host name is not valid.",
        "SocketHostNotFound" or "SocketNoData" => "The resolver reported that the name does not exist.",
        "SocketTryAgain" => "The resolver could not be reached.",
        _ => "The name could not be resolved.",
    };
}
