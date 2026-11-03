namespace NetTrace.Core.Configuration;

/// <summary>
/// User-configurable monitoring settings. Defaults follow plan.md sections 4.2-4.5. Every
/// endpoint here is disclosed to the user; none is contacted unless monitoring is enabled.
/// </summary>
public sealed record MonitoringConfiguration
{
    public static IReadOnlyList<int> AllowedIntervalsSeconds { get; } = [5, 10, 15, 30, 60];

    public bool MonitoringEnabled { get; init; } = true;

    /// <summary>Main cycle interval for gateway and external reachability probes.</summary>
    public int IntervalSeconds { get; init; } = 5;

    public int GatewayTimeoutMilliseconds { get; init; } = 1000;

    /// <summary>External ICMP targets. Multiple independent targets avoid trusting one endpoint.</summary>
    public IReadOnlyList<string> ExternalIcmpTargets { get; init; } = ["1.1.1.1", "8.8.8.8"];

    public int ExternalTimeoutMilliseconds { get; init; } = 2000;

    public int DnsIntervalSeconds { get; init; } = 30;

    public string DnsTestHostname { get; init; } = "example.com";

    public int DnsTimeoutMilliseconds { get; init; } = 3000;

    public int HttpsIntervalSeconds { get; init; } = 30;

    public IReadOnlyList<string> HttpsEndpoints { get; init; } =
    [
        "https://www.google.com/generate_204",
        "https://www.cloudflare.com/cdn-cgi/trace",
    ];

    public int HttpsTimeoutMilliseconds { get; init; } = 5000;

    /// <summary>Consecutive failed cycles required before an incident is confirmed.</summary>
    public int OutageConfirmationThreshold { get; init; } = 3;

    /// <summary>Consecutive healthy cycles required before an incident is closed.</summary>
    public int RecoveryConfirmationThreshold { get; init; } = 3;

    /// <summary>Failures that resume within this window after a recovery merge into the same incident.</summary>
    public int IncidentMergeWindowSeconds { get; init; } = 60;

    /// <summary>Public IP lookup contacts an external service, so it is off unless the user enables it.</summary>
    public bool PublicIpLookupEnabled { get; init; }

    public int PublicIpLookupIntervalMinutes { get; init; } = 5;
}
