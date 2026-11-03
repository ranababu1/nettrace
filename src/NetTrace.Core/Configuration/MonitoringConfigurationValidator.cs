using System.Net;

namespace NetTrace.Core.Configuration;

public static class MonitoringConfigurationValidator
{
    public const int MinimumTimeoutMilliseconds = 100;
    public const int MaximumTimeoutMilliseconds = 30_000;
    public const int MaximumSlowProbeIntervalSeconds = 3600;
    public const int MaximumThreshold = 20;
    public const int MaximumTargets = 10;

    /// <summary>Returns human-readable validation errors; an empty list means the configuration is valid.</summary>
    public static IReadOnlyList<string> Validate(MonitoringConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var errors = new List<string>();
        var c = configuration;

        if (!MonitoringConfiguration.AllowedIntervalsSeconds.Contains(c.IntervalSeconds))
        {
            errors.Add($"Monitoring interval must be one of {string.Join(", ", MonitoringConfiguration.AllowedIntervalsSeconds)} seconds.");
        }

        // Probes that run every cycle must finish within the cycle so cycles never overlap.
        var cycleMilliseconds = c.IntervalSeconds * 1000;
        ValidateTimeout(errors, "Gateway", c.GatewayTimeoutMilliseconds, cycleMilliseconds);
        ValidateTimeout(errors, "External", c.ExternalTimeoutMilliseconds, cycleMilliseconds);
        ValidateTimeout(errors, "DNS", c.DnsTimeoutMilliseconds, c.DnsIntervalSeconds * 1000);
        ValidateTimeout(errors, "HTTPS", c.HttpsTimeoutMilliseconds, c.HttpsIntervalSeconds * 1000);

        ValidateSlowInterval(errors, "DNS", c.DnsIntervalSeconds, c.IntervalSeconds);
        ValidateSlowInterval(errors, "HTTPS", c.HttpsIntervalSeconds, c.IntervalSeconds);

        ValidateList(errors, "External ICMP target", c.ExternalIcmpTargets, target =>
            IPAddress.TryParse(target, out _) ? null : $"External ICMP target '{target}' must be an IP address.");

        if (string.IsNullOrWhiteSpace(c.DnsTestHostname)
            || Uri.CheckHostName(c.DnsTestHostname) != UriHostNameType.Dns
            || !c.DnsTestHostname.Contains('.', StringComparison.Ordinal))
        {
            errors.Add($"DNS test hostname '{c.DnsTestHostname}' must be a fully qualified host name.");
        }

        ValidateList(errors, "HTTPS endpoint", c.HttpsEndpoints, endpoint =>
            Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && string.IsNullOrEmpty(uri.UserInfo)
                ? null
                : $"HTTPS endpoint '{endpoint}' must be an absolute https:// URL without credentials.");

        ValidateRange(errors, "Outage confirmation threshold", c.OutageConfirmationThreshold, 2, MaximumThreshold);
        ValidateRange(errors, "Recovery confirmation threshold", c.RecoveryConfirmationThreshold, 1, MaximumThreshold);
        ValidateRange(errors, "Incident merge window", c.IncidentMergeWindowSeconds, 0, 3600);
        ValidateRange(errors, "Public IP lookup interval", c.PublicIpLookupIntervalMinutes, 5, 1440);

        return errors;
    }

    private static void ValidateTimeout(List<string> errors, string probe, int timeoutMilliseconds, int intervalMilliseconds)
    {
        if (timeoutMilliseconds < MinimumTimeoutMilliseconds || timeoutMilliseconds > MaximumTimeoutMilliseconds)
        {
            errors.Add($"{probe} timeout must be between {MinimumTimeoutMilliseconds} and {MaximumTimeoutMilliseconds} ms.");
        }
        else if (intervalMilliseconds > 0 && timeoutMilliseconds >= intervalMilliseconds)
        {
            errors.Add($"{probe} timeout must be shorter than its probe interval.");
        }
    }

    private static void ValidateSlowInterval(List<string> errors, string probe, int intervalSeconds, int cycleSeconds)
    {
        if (intervalSeconds < cycleSeconds || intervalSeconds > MaximumSlowProbeIntervalSeconds)
        {
            errors.Add($"{probe} interval must be between the monitoring interval ({cycleSeconds} s) and {MaximumSlowProbeIntervalSeconds} s.");
        }
    }

    private static void ValidateList(
        List<string> errors, string label, IReadOnlyList<string>? values, Func<string, string?> validateItem)
    {
        if (values is null || values.Count == 0)
        {
            errors.Add($"At least one {label} is required.");
            return;
        }

        if (values.Count > MaximumTargets)
        {
            errors.Add($"No more than {MaximumTargets} {label}s are allowed.");
        }

        if (values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != values.Count)
        {
            errors.Add($"Each {label} must be listed only once.");
        }

        foreach (var value in values)
        {
            if (validateItem(value ?? string.Empty) is { } error)
            {
                errors.Add(error);
            }
        }
    }

    private static void ValidateRange(List<string> errors, string label, int value, int minimum, int maximum)
    {
        if (value < minimum || value > maximum)
        {
            errors.Add($"{label} must be between {minimum} and {maximum}.");
        }
    }
}
