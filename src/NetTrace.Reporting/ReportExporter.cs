using System.Globalization;
using System.Text;
using System.Text.Json;
using NetTrace.Core.Incidents;
using NetTrace.Core.Monitoring;
using NetTrace.Core.Probes;
using NetTrace.Core.Statistics;
using NetTrace.Reporting.Integrity;

namespace NetTrace.Reporting;

public sealed record ExportResult(string Directory, IReadOnlyList<ManifestFile> Files);

/// <summary>
/// Writes a report bundle: the data file(s) plus <c>manifest.json</c> with each file's size and
/// SHA-256. Observations are read and written one local day at a time, so memory stays bounded
/// however long the history. All timestamps are ISO 8601; UTC is authoritative.
/// </summary>
public sealed class ReportExporter(
    IObservationRepository observations, IIncidentRepository incidents, IMonitoringGapRepository gaps)
{
    public const string ReportSchemaVersion = "1.0";
    public const string JsonFileName = "report.json";
    public const int MaximumDays = 3660;

    internal const string UptimeDefinition =
        "Healthy time divided by monitored time. Monitored time excludes monitoring gaps (application not running, "
        + "system asleep, monitoring disabled). Unmonitored time is counted as neither uptime nor downtime.";

    internal const string ClassificationNote =
        "Incident classifications are diagnostic evidence derived from the listed probes. They are not a "
        + "definitive attribution of fault to an internet provider.";

    private sealed record DayResult(
        DateOnly Date, DateTimeOffset StartUtc, DateTimeOffset EndUtc, DailyStatistics Stats, int ObservationCount, int ErrorCount, int SkippedCount);

    public async Task<ExportResult> ExportAsync(
        ReportRequest request,
        ReportEnvironment environment,
        string outputRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);
        if (request.LastLocalDate < request.FirstLocalDate)
        {
            throw new ArgumentException("The last day must not be before the first day.", nameof(request));
        }

        var dates = Enumerable.Range(0, request.LastLocalDate.DayNumber - request.FirstLocalDate.DayNumber + 1)
            .Select(i => request.FirstLocalDate.AddDays(i))
            .ToList();
        if (dates.Count > MaximumDays)
        {
            throw new ArgumentException($"A report can cover at most {MaximumDays} days.", nameof(request));
        }

        var windows = dates.Select(d => (Date: d, Start: DayStart(d, request.TimeZone), End: DayStart(d.AddDays(1), request.TimeZone))).ToList();
        var rangeStart = windows[0].Start;
        var rangeEnd = windows[^1].End;

        var allIncidents = await incidents.GetOverlappingAsync(rangeStart, rangeEnd, cancellationToken).ConfigureAwait(false);
        var allGaps = await gaps.GetOverlappingAsync(rangeStart, rangeEnd, cancellationToken).ConfigureAwait(false);

        var days = new List<DayResult>();
        foreach (var (date, start, end) in windows)
        {
            var dayObservations = await observations.GetObservationsAsync(start, end, cancellationToken).ConfigureAwait(false);
            var stats = DailyStatisticsCalculator.Calculate(start, end, environment.GeneratedAtUtc, dayObservations, allIncidents, allGaps);
            days.Add(new DayResult(
                date, start, end, stats, dayObservations.Count,
                dayObservations.Count(o => o.Result.Outcome == ProbeOutcome.Error),
                dayObservations.Count(o => o.Result.Outcome == ProbeOutcome.Skipped)));
        }

        var total = DailyStatisticsCalculator.Calculate(rangeStart, rangeEnd, environment.GeneratedAtUtc, [], allIncidents, allGaps);

        var directory = Path.Combine(
            outputRoot,
            string.Create(CultureInfo.InvariantCulture,
                $"NetTrace-report-{request.FirstLocalDate:yyyy-MM-dd}_{request.LastLocalDate:yyyy-MM-dd}-{environment.GeneratedAtUtc:yyyyMMdd'T'HHmmss'Z'}"));
        Directory.CreateDirectory(directory);

        var ctx = new Context(request, environment, windows.Select(w => (w.Start, w.End)).ToList(), allIncidents, allGaps, days, total, dates);
        var written = request.Format == ExportFormat.Json
            ? await WriteJsonAsync(directory, ctx, cancellationToken).ConfigureAwait(false)
            : await WriteCsvAsync(directory, ctx, cancellationToken).ConfigureAwait(false);

        var files = new List<ManifestFile>();
        foreach (var name in written)
        {
            var path = Path.Combine(directory, name);
            files.Add(new ManifestFile(name, new FileInfo(path).Length, await Sha256Checksum.ComputeFileAsync(path, cancellationToken).ConfigureAwait(false)));
        }

        var manifest = new ExportManifest(
            ExportManifestFile.SchemaVersion, Sha256Checksum.AlgorithmName, environment.GeneratedAtUtc, ExportManifestFile.IntegrityNote, files);
        await File.WriteAllTextAsync(
            Path.Combine(directory, ExportManifestFile.FileName),
            JsonSerializer.Serialize(manifest, ExportManifestFile.Options),
            new UTF8Encoding(false),
            cancellationToken).ConfigureAwait(false);

        return new ExportResult(directory, files);
    }

    private sealed record Context(
        ReportRequest Request,
        ReportEnvironment Environment,
        List<(DateTimeOffset Start, DateTimeOffset End)> Windows,
        IReadOnlyList<Incident> Incidents,
        IReadOnlyList<MonitoringGap> Gaps,
        List<DayResult> Days,
        DailyStatistics Total,
        List<DateOnly> Dates)
    {
        public bool Redact => Request.Privacy.RedactLocalAddresses;

        public string Local(DateTimeOffset utc) =>
            TimeZoneInfo.ConvertTime(utc, Request.TimeZone).ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture);
    }

    // ---------------------------------------------------------------- JSON

    private async Task<List<string>> WriteJsonAsync(string directory, Context ctx, CancellationToken ct)
    {
        await using (var stream = new FileStream(Path.Combine(directory, JsonFileName), FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
        await using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            var req = ctx.Request;
            var env = ctx.Environment;
            w.WriteStartObject();
            w.WriteString("schema_version", ReportSchemaVersion);
            w.WriteStartObject("application");
            w.WriteString("name", env.ApplicationName);
            w.WriteString("version", env.ApplicationVersion);
            w.WriteEndObject();

            w.WriteStartObject("report");
            w.WriteString("generated_at_utc", Utc(env.GeneratedAtUtc));
            w.WriteString("period_start_utc", Utc(ctx.Windows[0].Start));
            w.WriteString("period_end_utc", Utc(ctx.Windows[^1].End));
            w.WriteString("first_local_date", req.FirstLocalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            w.WriteString("last_local_date", req.LastLocalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            w.WriteString("timezone_id", req.TimeZone.Id);
            w.WriteString("detail", req.Detail.ToString());
            w.WriteBoolean("local_addresses_redacted", ctx.Redact);
            w.WriteEndObject();

            WriteMethodology(w, ctx);

            w.WriteStartObject("network_context");
            w.WriteBoolean("included", false);
            w.WriteString("reason", "Adapter and network snapshots are not included in this report version.");
            w.WriteEndObject();

            if (req.Detail != ReportDetail.IncidentsOnly)
            {
                WriteSummary(w, ctx);
            }

            w.WriteStartArray("incidents");
            foreach (var i in ctx.Incidents)
            {
                w.WriteStartObject();
                w.WriteString("id", i.Id.ToString("D"));
                w.WriteString("started_at_utc", Utc(i.StartedAtUtc));
                w.WriteString("started_at_local", ctx.Local(i.StartedAtUtc));
                w.WriteString("detected_at_utc", Utc(i.DetectedAtUtc));
                WriteNullableTime(w, "ended_at_utc", i.EndedAtUtc);
                if (i.EndedAtUtc is { } e)
                {
                    w.WriteNumber("duration_seconds", (e - i.StartedAtUtc).TotalSeconds);
                }
                else
                {
                    w.WriteNull("duration_seconds");
                    w.WriteBoolean("ongoing", true);
                }

                w.WriteString("classification", i.Classification.ToString());
                w.WriteString("confidence", i.Confidence.ToString());
                w.WriteString("summary", i.Summary);
                w.WriteStartArray("failed_probes");
                foreach (var p in i.FailedProbes)
                {
                    w.WriteStringValue(Redactor.Redact(p, ctx.Redact));
                }

                w.WriteEndArray();
                w.WriteStartArray("working_probes");
                foreach (var p in i.WorkingProbes)
                {
                    w.WriteStringValue(Redactor.Redact(p, ctx.Redact));
                }

                w.WriteEndArray();
                w.WriteEndObject();
            }

            w.WriteEndArray();

            w.WriteStartArray("monitoring_gaps");
            foreach (var g in ctx.Gaps)
            {
                w.WriteStartObject();
                w.WriteString("started_at_utc", Utc(g.StartedAtUtc));
                w.WriteString("started_at_local", ctx.Local(g.StartedAtUtc));
                WriteNullableTime(w, "ended_at_utc", g.EndedAtUtc);
                w.WriteString("reason", g.Reason.ToString());
                w.WriteString("details", g.Details);
                w.WriteEndObject();
            }

            w.WriteEndArray();

            w.WriteStartArray("data_quality_notes");
            foreach (var note in DataQualityNotes(ctx))
            {
                w.WriteStringValue(note);
            }

            w.WriteEndArray();

            if (req.Detail == ReportDetail.Detailed)
            {
                w.WriteStartArray("observations");
                await w.FlushAsync(ct).ConfigureAwait(false);
                foreach (var day in ctx.Days)
                {
                    foreach (var o in await observations.GetObservationsAsync(day.StartUtc, day.EndUtc, ct).ConfigureAwait(false))
                    {
                        var r = o.Result;
                        w.WriteStartObject();
                        w.WriteString("cycle_id", o.CycleId.ToString("D"));
                        w.WriteString("probe_type", r.ProbeType.ToString());
                        w.WriteString("target", Redactor.Redact(r.Target, ctx.Redact));
                        w.WriteString("method", r.Method);
                        w.WriteString("started_at_utc", Utc(r.StartedAtUtc));
                        w.WriteString("completed_at_utc", Utc(r.CompletedAtUtc));
                        w.WriteString("outcome", r.Outcome.ToString());
                        if (r.LatencyMs is { } l)
                        {
                            w.WriteNumber("latency_ms", l);
                        }
                        else
                        {
                            w.WriteNull("latency_ms");
                        }

                        w.WriteNumber("timeout_ms", r.TimeoutMilliseconds);
                        w.WriteString("error_code", r.ErrorCode);
                        w.WriteString("error_message", r.ErrorMessage);
                        w.WriteNumber("configuration_version", o.ConfigurationVersion);
                        w.WriteEndObject();
                    }

                    await w.FlushAsync(ct).ConfigureAwait(false);
                }

                w.WriteEndArray();
            }

            w.WriteStartObject("integrity");
            w.WriteString("algorithm", Sha256Checksum.AlgorithmName);
            w.WriteString("manifest_file", ExportManifestFile.FileName);
            w.WriteString("note", ExportManifestFile.IntegrityNote + " The hash of this file is listed in the manifest, because a file cannot contain its own hash.");
            w.WriteEndObject();
            w.WriteEndObject();
            await w.FlushAsync(ct).ConfigureAwait(false);
        }

        return [JsonFileName];
    }

    private static void WriteMethodology(Utf8JsonWriter w, Context ctx)
    {
        var c = ctx.Environment.Configuration;
        w.WriteStartObject("methodology");
        w.WriteNumber("monitoring_interval_seconds", c.IntervalSeconds);
        w.WriteNumber("gateway_timeout_ms", c.GatewayTimeoutMilliseconds);
        w.WriteNumber("external_timeout_ms", c.ExternalTimeoutMilliseconds);
        w.WriteStartArray("external_icmp_targets");
        foreach (var t in c.ExternalIcmpTargets)
        {
            w.WriteStringValue(t);
        }

        w.WriteEndArray();
        w.WriteNumber("dns_interval_seconds", c.DnsIntervalSeconds);
        w.WriteString("dns_test_hostname", c.DnsTestHostname);
        w.WriteNumber("dns_timeout_ms", c.DnsTimeoutMilliseconds);
        w.WriteNumber("https_interval_seconds", c.HttpsIntervalSeconds);
        w.WriteStartArray("https_endpoints");
        foreach (var t in c.HttpsEndpoints)
        {
            w.WriteStringValue(t);
        }

        w.WriteEndArray();
        w.WriteNumber("https_timeout_ms", c.HttpsTimeoutMilliseconds);
        w.WriteNumber("outage_confirmation_threshold", c.OutageConfirmationThreshold);
        w.WriteNumber("recovery_confirmation_threshold", c.RecoveryConfirmationThreshold);
        w.WriteNumber("incident_merge_window_seconds", c.IncidentMergeWindowSeconds);
        w.WriteString("uptime_definition", UptimeDefinition);
        w.WriteString("jitter_definition", "Mean absolute difference between consecutive successful latency samples for the same probe type and target.");
        w.WriteString("packet_loss_definition", "Failed or timed-out probes divided by eligible probes for the same probe type and target. Errors and skipped probes are not eligible.");
        w.WriteString("classification_note", ClassificationNote);
        w.WriteEndObject();
    }

    private static void WriteSummary(Utf8JsonWriter w, Context ctx)
    {
        w.WriteStartObject("summary");
        WriteStatisticsBody(w, ctx.Total, includeSeries: false, ctx);
        w.WriteNumber("dns_failure_count", ctx.Days.Sum(d => d.Stats.DnsFailureCount));
        w.WriteNumber("https_failure_count", ctx.Days.Sum(d => d.Stats.HttpsFailureCount));
        w.WriteEndObject();

        w.WriteStartArray("daily");
        foreach (var d in ctx.Days)
        {
            w.WriteStartObject();
            w.WriteString("date", d.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            w.WriteString("start_utc", Utc(d.StartUtc));
            w.WriteString("end_utc", Utc(d.EndUtc));
            WriteStatisticsBody(w, d.Stats, includeSeries: true, ctx);
            w.WriteNumber("dns_failure_count", d.Stats.DnsFailureCount);
            w.WriteNumber("https_failure_count", d.Stats.HttpsFailureCount);
            w.WriteNumber("observation_count", d.ObservationCount);
            w.WriteEndObject();
        }

        w.WriteEndArray();
    }

    private static void WriteStatisticsBody(Utf8JsonWriter w, DailyStatistics s, bool includeSeries, Context ctx)
    {
        w.WriteNumber("elapsed_seconds", s.ElapsedSeconds);
        w.WriteNumber("observed_seconds", s.ObservedSeconds);
        w.WriteNumber("healthy_seconds", s.HealthySeconds);
        w.WriteNumber("outage_seconds", s.OutageSeconds);
        w.WriteNumber("unmonitored_seconds", s.UnmonitoredSeconds);
        Nullable(w, "uptime_percentage", s.UptimePercentage);
        Nullable(w, "monitoring_coverage_percentage", s.MonitoringCoveragePercentage);
        w.WriteNumber("incident_count", s.IncidentCount);
        Nullable(w, "longest_incident_seconds", s.LongestIncidentSeconds);
        if (!includeSeries)
        {
            return;
        }

        w.WriteStartArray("probe_statistics");
        foreach (var p in s.Series)
        {
            w.WriteStartObject();
            w.WriteString("probe_type", p.ProbeType.ToString());
            w.WriteString("target", Redactor.Redact(p.Target, ctx.Redact));
            w.WriteNumber("eligible_probes", p.EligibleProbes);
            w.WriteNumber("failed_probes", p.FailedProbes);
            Nullable(w, "packet_loss_percentage", p.PacketLossPercentage);
            Nullable(w, "average_latency_ms", p.AverageLatencyMs);
            Nullable(w, "median_latency_ms", p.MedianLatencyMs);
            Nullable(w, "p95_latency_ms", p.P95LatencyMs);
            Nullable(w, "maximum_latency_ms", p.MaximumLatencyMs);
            Nullable(w, "jitter_ms", p.JitterMs);
            w.WriteEndObject();
        }

        w.WriteEndArray();
    }

    private static void Nullable(Utf8JsonWriter w, string name, double? value)
    {
        if (value is { } v)
        {
            w.WriteNumber(name, v);
        }
        else
        {
            w.WriteNull(name);
        }
    }

    private static void WriteNullableTime(Utf8JsonWriter w, string name, DateTimeOffset? utc)
    {
        if (utc is { } t)
        {
            w.WriteString(name, Utc(t));
        }
        else
        {
            w.WriteNull(name);
        }
    }

    // ---------------------------------------------------------------- CSV

    private async Task<List<string>> WriteCsvAsync(string directory, Context ctx, CancellationToken ct)
    {
        var written = new List<string>();

        await WriteCsvFileAsync(directory, "incidents.csv", written, ct, async w =>
        {
            await w.WriteLineAsync("id,started_at_utc,started_at_local,detected_at_utc,ended_at_utc,duration_seconds,classification,confidence,summary,failed_probes,working_probes").ConfigureAwait(false);
            foreach (var i in ctx.Incidents)
            {
                await w.WriteLineAsync(Row(
                    i.Id.ToString("D"), Utc(i.StartedAtUtc), ctx.Local(i.StartedAtUtc), Utc(i.DetectedAtUtc),
                    i.EndedAtUtc is { } e ? Utc(e) : string.Empty,
                    i.EndedAtUtc is { } e2 ? Num((e2 - i.StartedAtUtc).TotalSeconds) : string.Empty,
                    i.Classification.ToString(), i.Confidence.ToString(), i.Summary,
                    string.Join("; ", i.FailedProbes.Select(p => Redactor.Redact(p, ctx.Redact))),
                    string.Join("; ", i.WorkingProbes.Select(p => Redactor.Redact(p, ctx.Redact))))).ConfigureAwait(false);
            }
        });

        await WriteCsvFileAsync(directory, "monitoring_gaps.csv", written, ct, async w =>
        {
            await w.WriteLineAsync("started_at_utc,started_at_local,ended_at_utc,reason,details").ConfigureAwait(false);
            foreach (var g in ctx.Gaps)
            {
                await w.WriteLineAsync(Row(
                    Utc(g.StartedAtUtc), ctx.Local(g.StartedAtUtc), g.EndedAtUtc is { } e ? Utc(e) : string.Empty,
                    g.Reason.ToString(), g.Details ?? string.Empty)).ConfigureAwait(false);
            }
        });

        if (ctx.Request.Detail != ReportDetail.IncidentsOnly)
        {
            await WriteCsvFileAsync(directory, "daily.csv", written, ct, async w =>
            {
                await w.WriteLineAsync("date,elapsed_seconds,observed_seconds,healthy_seconds,outage_seconds,unmonitored_seconds,uptime_percentage,monitoring_coverage_percentage,incident_count,longest_incident_seconds,dns_failure_count,https_failure_count,observation_count").ConfigureAwait(false);
                foreach (var d in ctx.Days)
                {
                    var s = d.Stats;
                    await w.WriteLineAsync(Row(
                        d.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Num(s.ElapsedSeconds), Num(s.ObservedSeconds),
                        Num(s.HealthySeconds), Num(s.OutageSeconds), Num(s.UnmonitoredSeconds), Num(s.UptimePercentage),
                        Num(s.MonitoringCoveragePercentage), s.IncidentCount.ToString(CultureInfo.InvariantCulture), Num(s.LongestIncidentSeconds),
                        s.DnsFailureCount.ToString(CultureInfo.InvariantCulture), s.HttpsFailureCount.ToString(CultureInfo.InvariantCulture),
                        d.ObservationCount.ToString(CultureInfo.InvariantCulture))).ConfigureAwait(false);
                }
            });

            await WriteCsvFileAsync(directory, "probe_statistics.csv", written, ct, async w =>
            {
                await w.WriteLineAsync("date,probe_type,target,eligible_probes,failed_probes,packet_loss_percentage,average_latency_ms,median_latency_ms,p95_latency_ms,maximum_latency_ms,jitter_ms").ConfigureAwait(false);
                foreach (var d in ctx.Days)
                {
                    foreach (var p in d.Stats.Series)
                    {
                        await w.WriteLineAsync(Row(
                            d.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), p.ProbeType.ToString(), Redactor.Redact(p.Target, ctx.Redact),
                            p.EligibleProbes.ToString(CultureInfo.InvariantCulture), p.FailedProbes.ToString(CultureInfo.InvariantCulture),
                            Num(p.PacketLossPercentage), Num(p.AverageLatencyMs), Num(p.MedianLatencyMs), Num(p.P95LatencyMs),
                            Num(p.MaximumLatencyMs), Num(p.JitterMs))).ConfigureAwait(false);
                    }
                }
            });
        }

        if (ctx.Request.Detail == ReportDetail.Detailed)
        {
            await WriteCsvFileAsync(directory, "observations.csv", written, ct, async w =>
            {
                await w.WriteLineAsync("cycle_id,probe_type,target,method,started_at_utc,completed_at_utc,outcome,latency_ms,timeout_ms,error_code,error_message,configuration_version").ConfigureAwait(false);
                foreach (var day in ctx.Days)
                {
                    foreach (var o in await observations.GetObservationsAsync(day.StartUtc, day.EndUtc, ct).ConfigureAwait(false))
                    {
                        var r = o.Result;
                        await w.WriteLineAsync(Row(
                            o.CycleId.ToString("D"), r.ProbeType.ToString(), Redactor.Redact(r.Target, ctx.Redact), r.Method,
                            Utc(r.StartedAtUtc), Utc(r.CompletedAtUtc), r.Outcome.ToString(), Num(r.LatencyMs),
                            r.TimeoutMilliseconds.ToString(CultureInfo.InvariantCulture), r.ErrorCode ?? string.Empty,
                            r.ErrorMessage ?? string.Empty, o.ConfigurationVersion.ToString(CultureInfo.InvariantCulture))).ConfigureAwait(false);
                    }
                }
            });
        }

        // CSV has no place for methodology, so it travels as a plain-text companion file.
        await File.WriteAllTextAsync(Path.Combine(directory, "README.txt"), BuildReadme(ctx), new UTF8Encoding(false), ct).ConfigureAwait(false);
        written.Add("README.txt");
        return written;
    }

    private static async Task WriteCsvFileAsync(
        string directory, string name, List<string> written, CancellationToken ct, Func<StreamWriter, Task> body)
    {
        await using var stream = new FileStream(Path.Combine(directory, name), FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\r\n" };
        await body(writer).ConfigureAwait(false);
        await writer.FlushAsync(ct).ConfigureAwait(false);
        written.Add(name);
    }

    private static string BuildReadme(Context ctx)
    {
        var c = ctx.Environment.Configuration;
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"{ctx.Environment.ApplicationName} {ctx.Environment.ApplicationVersion} report");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Generated (UTC): {Utc(ctx.Environment.GeneratedAtUtc)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Period: {ctx.Request.FirstLocalDate:yyyy-MM-dd} to {ctx.Request.LastLocalDate:yyyy-MM-dd} ({ctx.Request.TimeZone.Id}); UTC {Utc(ctx.Windows[0].Start)} to {Utc(ctx.Windows[^1].End)}");
        sb.AppendLine("All timestamps are ISO 8601. UTC timestamps are authoritative; local timestamps are for readability.");
        sb.AppendLine();
        sb.AppendLine("Methodology");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Monitoring interval: {c.IntervalSeconds} s; outage confirmed after {c.OutageConfirmationThreshold} failed cycles, closed after {c.RecoveryConfirmationThreshold} healthy cycles; merge window {c.IncidentMergeWindowSeconds} s.");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- External ICMP targets: {string.Join(", ", c.ExternalIcmpTargets)}; DNS test name: {c.DnsTestHostname}; HTTPS endpoints: {string.Join(", ", c.HttpsEndpoints)}.");
        sb.AppendLine("- Uptime: " + UptimeDefinition);
        sb.AppendLine("- " + ClassificationNote);
        sb.AppendLine();
        sb.AppendLine("Data quality notes");
        foreach (var note in DataQualityNotes(ctx))
        {
            sb.AppendLine("- " + note);
        }

        sb.AppendLine();
        sb.AppendLine("Integrity");
        sb.AppendLine("- " + ExportManifestFile.IntegrityNote);
        sb.AppendLine("- File sizes and SHA-256 hashes are listed in " + ExportManifestFile.FileName + ".");
        return sb.ToString();
    }

    // ---------------------------------------------------------------- shared

    private static List<string> DataQualityNotes(Context ctx)
    {
        var notes = new List<string>();
        var unmonitored = ctx.Total.UnmonitoredSeconds;
        if (ctx.Gaps.Count > 0)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture,
                $"{ctx.Gaps.Count} monitoring gap(s) overlap this period, totalling {DurationFormatter.Format(unmonitored)} of unmonitored time. Network conditions during gaps are unknown and are not counted as uptime or downtime."));
        }
        else
        {
            notes.Add("No monitoring gaps were recorded in this period.");
        }

        var errors = ctx.Days.Sum(d => d.ErrorCount);
        var skipped = ctx.Days.Sum(d => d.SkippedCount);
        if (errors > 0)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"{errors} probe attempt(s) could not be executed (outcome Error) and are excluded from packet-loss figures."));
        }

        if (skipped > 0)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"{skipped} probe(s) were skipped because they could not be attempted (for example, no default gateway); they are not failures."));
        }

        if (ctx.Total.ElapsedSeconds < (ctx.Windows[^1].End - ctx.Windows[0].Start).TotalSeconds)
        {
            notes.Add("The period extends beyond the report time; only time up to the report time is counted.");
        }

        if (ctx.Redact)
        {
            notes.Add("Private and link-local IP addresses were replaced with a placeholder.");
        }

        if (ctx.Request.Detail != ReportDetail.Detailed)
        {
            notes.Add("Raw probe observations are not included in this report.");
        }

        notes.Add("Measurement values that were unavailable are null or empty, never zero.");
        return notes;
    }

    private static DateTimeOffset DayStart(DateOnly date, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        // Some zones skip local midnight on DST change days; use the first valid instant.
        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(30);
        }

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
    }

    private static string Utc(DateTimeOffset t) =>
        t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    private static string Num(double? value) => value is { } v ? v.ToString("R", CultureInfo.InvariantCulture) : string.Empty;

    private static string Row(params string[] fields) => string.Join(',', fields.Select(Csv.Escape));
}

internal static class Csv
{
    /// <summary>RFC 4180 quoting, plus a leading apostrophe on values a spreadsheet would run as a formula.</summary>
    public static string Escape(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '@' or '\t' or '\r' || (value.Length > 0 && value[0] == '-' && !IsNumber(value)))
        {
            value = "'" + value;
        }

        return value.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
    }

    private static bool IsNumber(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
}
