using NetTrace.Core.Configuration;
using NetTrace.Core.Network;
using NetTrace.Core.Probes;
using NetTrace.Core.Time;

namespace NetTrace.Core.Monitoring;

/// <summary>One completed monitoring cycle with all of its probe results.</summary>
public sealed record MonitoringCycle(
    Guid CycleId,
    DateTimeOffset ScheduledAtUtc,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    int ConfigurationVersion,
    NetworkContext NetworkContext,
    IReadOnlyList<ProbeResult> Results);

/// <summary>
/// Runs the probes that belong to every cycle: the default gateway and each external ICMP
/// target, concurrently. Concurrency is bounded by the validated target count, and every probe
/// carries its own timeout, which validation keeps shorter than the cycle interval.
/// </summary>
public sealed class MonitoringCycleRunner(
    IIcmpProbe icmpProbe, IClock clock, IDnsProbe? dnsProbe = null, IHttpsProbe? httpsProbe = null)
{
    private DateTimeOffset? _lastDnsAt;
    private DateTimeOffset? _lastHttpsAt;
    private readonly List<Task<ProbeResult>> _pendingSlowProbes = [];

    public const string IcmpMethod = "ICMP echo";
    public const string NoTarget = "(none)";

    public async Task<MonitoringCycle> RunAsync(
        MonitoringConfiguration configuration,
        int configurationVersion,
        NetworkContext networkContext,
        DateTimeOffset scheduledAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(networkContext);

        var startedAt = clock.UtcNow;
        var probes = new List<Task<ProbeResult>>(configuration.ExternalIcmpTargets.Count + 1);

        if (networkContext.DefaultGateway is { } gateway)
        {
            probes.Add(PingSafelyAsync(ProbeType.Gateway, gateway, configuration.GatewayTimeoutMilliseconds, cancellationToken));
        }
        else
        {
            // Record explicitly that the gateway could not be measured; this is not a gateway failure.
            probes.Add(Task.FromResult(ProbeResult.Skip(
                ProbeType.Gateway,
                NoTarget,
                IcmpMethod,
                startedAt,
                configuration.GatewayTimeoutMilliseconds,
                "NoActiveConnection",
                "No connected network adapter with a default gateway was found.")));
        }

        foreach (var target in configuration.ExternalIcmpTargets)
        {
            probes.Add(PingSafelyAsync(ProbeType.ExternalIcmp, target, configuration.ExternalTimeoutMilliseconds, cancellationToken));
        }

        if (dnsProbe is not null && IsDue(ref _lastDnsAt, scheduledAtUtc, configuration.DnsIntervalSeconds))
        {
            _pendingSlowProbes.Add(RunSafelyAsync(
                ProbeType.Dns, configuration.DnsTestHostname, "System resolver", configuration.DnsTimeoutMilliseconds,
                ct => dnsProbe.ResolveAsync(configuration.DnsTestHostname, configuration.DnsTimeoutMilliseconds, ct),
                cancellationToken));
        }

        if (httpsProbe is not null && IsDue(ref _lastHttpsAt, scheduledAtUtc, configuration.HttpsIntervalSeconds))
        {
            foreach (var endpoint in configuration.HttpsEndpoints)
            {
                _pendingSlowProbes.Add(RunSafelyAsync(
                    ProbeType.Https, endpoint, "HTTPS GET", configuration.HttpsTimeoutMilliseconds,
                    ct => httpsProbe.GetAsync(endpoint, configuration.HttpsTimeoutMilliseconds, ct),
                    cancellationToken));
            }
        }

        var fastResults = await Task.WhenAll(probes).ConfigureAwait(false);

        // Slow probes can outlast the cycle (their timeouts may exceed it), so they never delay it:
        // each is reported by the first cycle that finds it finished.
        var results = new List<ProbeResult>(fastResults);
        foreach (var finished in _pendingSlowProbes.Where(t => t.IsCompleted).ToList())
        {
            _pendingSlowProbes.Remove(finished);
            results.Add(await finished.ConfigureAwait(false));
        }

        return new MonitoringCycle(
            Guid.NewGuid(),
            scheduledAtUtc,
            startedAt,
            clock.UtcNow,
            configurationVersion,
            networkContext,
            results);
    }

    /// <summary>A slow probe runs on the first cycle and whenever its interval has elapsed since it last ran.</summary>
    private static bool IsDue(ref DateTimeOffset? last, DateTimeOffset scheduledAtUtc, int intervalSeconds)
    {
        if (last is { } previous && scheduledAtUtc >= previous && scheduledAtUtc - previous < TimeSpan.FromSeconds(intervalSeconds))
        {
            return false;
        }

        last = scheduledAtUtc;
        return true;
    }

    private Task<ProbeResult> PingSafelyAsync(
        ProbeType probeType, string target, int timeoutMilliseconds, CancellationToken cancellationToken) =>
        RunSafelyAsync(
            probeType, target, IcmpMethod, timeoutMilliseconds,
            ct => icmpProbe.PingAsync(probeType, target, timeoutMilliseconds, ct), cancellationToken);

    private async Task<ProbeResult> RunSafelyAsync(
        ProbeType probeType, string target, string method, int timeoutMilliseconds,
        Func<CancellationToken, Task<ProbeResult>> probe, CancellationToken cancellationToken)
    {
        var startedAt = clock.UtcNow;
        try
        {
            return await probe(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Probes report network failures in their result. Anything else is a bug; record it
            // as an error for this probe rather than losing the whole cycle.
            return new ProbeResult(
                probeType,
                target,
                method,
                startedAt,
                clock.UtcNow,
                ProbeOutcome.Error,
                LatencyMs: null,
                timeoutMilliseconds,
                "UnexpectedProbeError",
                "The probe failed unexpectedly.");
        }
    }
}
