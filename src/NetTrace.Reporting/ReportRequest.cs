using NetTrace.Core.Configuration;

namespace NetTrace.Reporting;

/// <summary>How much of the data a report contains. Serialized by name; never renumber or rename.</summary>
public enum ReportDetail
{
    /// <summary>Methodology, daily and overall statistics, incidents and gaps. No raw observations.</summary>
    Summary,

    /// <summary>Methodology, incidents and gaps only.</summary>
    IncidentsOnly,

    /// <summary>Everything in <see cref="Summary"/> plus every raw probe observation.</summary>
    Detailed,
}

public enum ExportFormat
{
    Json,

    /// <summary>A bundle of CSV files (observations, incidents, gaps, daily) plus the manifest.</summary>
    Csv,
}

/// <summary>What to hide from the report. Local addresses are redacted by default.</summary>
public sealed record ReportPrivacy(bool RedactLocalAddresses = true)
{
    public static ReportPrivacy Default { get; } = new();
}

/// <param name="FirstLocalDate">First day, inclusive, in <paramref name="TimeZone"/>.</param>
/// <param name="LastLocalDate">Last day, inclusive.</param>
public sealed record ReportRequest(
    DateOnly FirstLocalDate,
    DateOnly LastLocalDate,
    TimeZoneInfo TimeZone,
    ReportDetail Detail,
    ExportFormat Format,
    ReportPrivacy Privacy);

/// <summary>Facts about the producing application and moment, supplied by the caller (never read from ambient state).</summary>
public sealed record ReportEnvironment(
    string ApplicationName,
    string ApplicationVersion,
    DateTimeOffset GeneratedAtUtc,
    MonitoringConfiguration Configuration);
