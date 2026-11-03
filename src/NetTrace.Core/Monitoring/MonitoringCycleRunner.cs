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
public sealed class MonitoringCycleRunner(IIcmpProbe icmpProbe, IClock clock)
{
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

        var results = await Task.WhenAll(probes).ConfigureAwait(false);
        return new MonitoringCycle(
            Guid.NewGuid(),
            scheduledAtUtc,
            startedAt,
            clock.UtcNow,
            configurationVersion,
            networkContext,
            results);
    }

    private async Task<ProbeResult> PingSafelyAsync(
        ProbeType probeType, string target, int timeoutMilliseconds, CancellationToken cancellationToken)
    {
        var startedAt = clock.UtcNow;
        try
        {
            return await icmpProbe.PingAsync(probeType, target, timeoutMilliseconds, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Probes report network failures in their result. Anything else is a bug; record it
            // as an error for this probe rather than losing the whole cycle.
            return new ProbeResult(
                probeType,
                target,
                IcmpMethod,
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
