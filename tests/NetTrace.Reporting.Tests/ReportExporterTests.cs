using System.Text.Json;
using NetTrace.Core.Configuration;
using NetTrace.Core.Incidents;
using NetTrace.Core.Monitoring;
using NetTrace.Core.Probes;

namespace NetTrace.Reporting.Tests;

public sealed class ReportExporterTests : IDisposable
{
    private static readonly DateTimeOffset Day1 = new(2026, 11, 3, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Generated = new(2026, 11, 6, 8, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "NetTraceTests", Guid.NewGuid().ToString("N"));
    private readonly FakeData _data = new();

    public ReportExporterTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Json_ContainsRequiredSections_AndNullsForUnavailableValues()
    {
        _data.Add(Obs(ProbeType.ExternalIcmp, "1.1.1.1", ProbeOutcome.Success, 12.5, Day1.AddHours(1)));
        _data.Add(Obs(ProbeType.ExternalIcmp, "1.1.1.1", ProbeOutcome.Timeout, null, Day1.AddHours(2)));
        _data.Incidents.Add(Incident(Day1.AddHours(5), Day1.AddHours(6)));
        _data.Gaps.Add(new MonitoringGap(Day1.AddHours(8), Day1.AddHours(9), MonitoringGapReason.SystemSleep, "slept", "t"));

        var result = await Export(Request(ExportFormat.Json, ReportDetail.Detailed));

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(result.Directory, "report.json")));
        var root = doc.RootElement;
        Assert.Equal("1.0", root.GetProperty("schema_version").GetString());
        Assert.Equal("UTC", root.GetProperty("report").GetProperty("timezone_id").GetString());
        Assert.Equal("2026-11-03T00:00:00.0000000Z", root.GetProperty("report").GetProperty("period_start_utc").GetString());
        Assert.Equal(5, root.GetProperty("methodology").GetProperty("monitoring_interval_seconds").GetInt32());
        Assert.Equal(1, root.GetProperty("incidents").GetArrayLength());
        Assert.Equal("SystemSleep", root.GetProperty("monitoring_gaps")[0].GetProperty("reason").GetString());
        Assert.Equal(1, root.GetProperty("summary").GetProperty("incident_count").GetInt32());
        Assert.Equal(3600, root.GetProperty("summary").GetProperty("unmonitored_seconds").GetDouble());

        var observations = root.GetProperty("observations");
        Assert.Equal(2, observations.GetArrayLength());
        Assert.Equal(12.5, observations[0].GetProperty("latency_ms").GetDouble());
        Assert.Equal(JsonValueKind.Null, observations[1].GetProperty("latency_ms").ValueKind);
        Assert.True(root.GetProperty("data_quality_notes").GetArrayLength() > 0);
    }

    [Fact]
    public async Task SummaryDetail_OmitsObservations_AndIncidentsOnlyOmitsStatistics()
    {
        _data.Add(Obs(ProbeType.Dns, "example.com", ProbeOutcome.Success, 9, Day1.AddHours(1)));

        var summary = await ReadJson(await Export(Request(ExportFormat.Json, ReportDetail.Summary)));
        var incidentsOnly = await ReadJson(await Export(Request(ExportFormat.Json, ReportDetail.IncidentsOnly) with { LastLocalDate = new DateOnly(2026, 11, 3) }, generated: Generated.AddMinutes(1)));

        Assert.False(summary.RootElement.TryGetProperty("observations", out _));
        Assert.True(summary.RootElement.TryGetProperty("summary", out _));
        Assert.False(incidentsOnly.RootElement.TryGetProperty("summary", out _));
        Assert.True(incidentsOnly.RootElement.TryGetProperty("incidents", out _));
    }

    [Fact]
    public async Task LocalAddresses_AreRedactedByDefault_AndKeptWhenAsked()
    {
        _data.Add(Obs(ProbeType.Gateway, "192.168.1.1", ProbeOutcome.Success, 2, Day1.AddHours(1)));
        _data.Incidents.Add(Incident(Day1.AddHours(5), null) with { FailedProbes = ["Gateway 192.168.1.1", "ExternalIcmp 1.1.1.1"] });

        var redacted = await Export(Request(ExportFormat.Json, ReportDetail.Detailed));
        var kept = await Export(Request(ExportFormat.Json, ReportDetail.Detailed, new ReportPrivacy(false)), generated: Generated.AddMinutes(1));

        var redactedText = await File.ReadAllTextAsync(Path.Combine(redacted.Directory, "report.json"));
        var keptText = await File.ReadAllTextAsync(Path.Combine(kept.Directory, "report.json"));
        Assert.DoesNotContain("192.168.1.1", redactedText, StringComparison.Ordinal);
        Assert.Contains("1.1.1.1", redactedText, StringComparison.Ordinal);
        Assert.Contains("192.168.1.1", keptText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Csv_WritesSeparateFilesWithHeadersAndOneRowPerRecord()
    {
        _data.Add(Obs(ProbeType.ExternalIcmp, "8.8.8.8", ProbeOutcome.Success, 20, Day1.AddHours(1)));
        _data.Add(Obs(ProbeType.ExternalIcmp, "8.8.8.8", ProbeOutcome.Failure, null, Day1.AddHours(2)));
        _data.Incidents.Add(Incident(Day1.AddHours(5), Day1.AddHours(6)));

        var result = await Export(Request(ExportFormat.Csv, ReportDetail.Detailed));

        var names = result.Files.Select(f => f.Name).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["README.txt", "daily.csv", "incidents.csv", "monitoring_gaps.csv", "observations.csv", "probe_statistics.csv"], names);
        var observations = await File.ReadAllLinesAsync(Path.Combine(result.Directory, "observations.csv"));
        Assert.Equal(3, observations.Length);
        Assert.StartsWith("cycle_id,probe_type,target", observations[0], StringComparison.Ordinal);
        Assert.Equal(2, (await File.ReadAllLinesAsync(Path.Combine(result.Directory, "incidents.csv"))).Length);
        Assert.Contains("Methodology", await File.ReadAllTextAsync(Path.Combine(result.Directory, "README.txt")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Csv_NeutralisesSpreadsheetFormulasAndQuotesSpecialCharacters()
    {
        _data.Incidents.Add(Incident(Day1.AddHours(5), null) with { Summary = "=HYPERLINK(\"x\"),line" });

        var result = await Export(Request(ExportFormat.Csv, ReportDetail.Summary));

        var text = await File.ReadAllTextAsync(Path.Combine(result.Directory, "incidents.csv"));
        Assert.Contains("\"'=HYPERLINK(\"\"x\"\"),line\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Manifest_ListsEveryFileWithMatchingHash_AndDetectsTampering()
    {
        _data.Add(Obs(ProbeType.Dns, "example.com", ProbeOutcome.Success, 9, Day1.AddHours(1)));
        var result = await Export(Request(ExportFormat.Json, ReportDetail.Detailed));

        var ok = await ExportManifestFile.VerifyAsync(result.Directory);
        Assert.All(ok, v => Assert.True(v.Matches));
        Assert.Single(ok);

        var path = Path.Combine(result.Directory, "report.json");
        await File.AppendAllTextAsync(path, " ");
        var tampered = await ExportManifestFile.VerifyAsync(result.Directory);
        Assert.False(Assert.Single(tampered).Matches);

        File.Delete(path);
        Assert.Equal("The file is missing.", Assert.Single(await ExportManifestFile.VerifyAsync(result.Directory)).Problem);
    }

    [Fact]
    public async Task MultiDayRange_ProducesOneDailyEntryPerDay_AndLoadsObservationsPerDay()
    {
        _data.Add(Obs(ProbeType.Dns, "example.com", ProbeOutcome.Success, 9, Day1.AddHours(1)));
        _data.Add(Obs(ProbeType.Dns, "example.com", ProbeOutcome.Failure, null, Day1.AddDays(2).AddHours(1)));

        var result = await Export(Request(ExportFormat.Json, ReportDetail.Summary) with { LastLocalDate = new DateOnly(2026, 11, 5) });

        using var doc = await ReadJson(result);
        var daily = doc.RootElement.GetProperty("daily");
        Assert.Equal(3, daily.GetArrayLength());
        Assert.Equal(0, daily[0].GetProperty("dns_failure_count").GetInt32());
        Assert.Equal(1, daily[2].GetProperty("dns_failure_count").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("summary").GetProperty("dns_failure_count").GetInt32());
        Assert.All(_data.ObservationQueries, q => Assert.Equal(TimeSpan.FromDays(1), q.To - q.From));
    }

    [Fact]
    public async Task TimeZone_DefinesDayBoundaries()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test+05:30", TimeSpan.FromHours(5.5), "Test", "Test");
        var request = Request(ExportFormat.Json, ReportDetail.Summary) with { TimeZone = zone };

        using var doc = await ReadJson(await Export(request));

        Assert.Equal("2026-11-02T18:30:00.0000000Z", doc.RootElement.GetProperty("report").GetProperty("period_start_utc").GetString());
        Assert.Equal("Test+05:30", doc.RootElement.GetProperty("report").GetProperty("timezone_id").GetString());
    }

    [Fact]
    public async Task InvalidRange_IsRejected()
    {
        var bad = Request(ExportFormat.Json, ReportDetail.Summary) with { LastLocalDate = new DateOnly(2026, 11, 1) };

        await Assert.ThrowsAsync<ArgumentException>(() => Export(bad));
    }

    [Fact]
    public void Redactor_OnlyTouchesPrivateAndLinkLocalAddresses()
    {
        Assert.Equal(Redactor.Placeholder, Redactor.Redact("10.1.2.3", true));
        Assert.Equal(Redactor.Placeholder, Redactor.Redact("172.20.0.1", true));
        Assert.Equal("172.32.0.1", Redactor.Redact("172.32.0.1", true));
        Assert.Equal("8.8.8.8", Redactor.Redact("8.8.8.8", true));
        Assert.Equal($"Gateway {Redactor.Placeholder} down", Redactor.Redact("Gateway 169.254.1.1 down", true));
        Assert.Equal("192.168.1.1", Redactor.Redact("192.168.1.1", false));
    }

    // ---- helpers ----

    private Task<ExportResult> Export(ReportRequest request, DateTimeOffset? generated = null) =>
        new ReportExporter(_data, _data, _data).ExportAsync(
            request, new ReportEnvironment("NetTrace", "1.2.3", generated ?? Generated, new MonitoringConfiguration()), _root);

    private static async Task<JsonDocument> ReadJson(ExportResult result) =>
        JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(result.Directory, "report.json")));

    private static ReportRequest Request(ExportFormat format, ReportDetail detail, ReportPrivacy? privacy = null) =>
        new(new DateOnly(2026, 11, 3), new DateOnly(2026, 11, 3), TimeZoneInfo.Utc, detail, format, privacy ?? ReportPrivacy.Default);

    private static Incident Incident(DateTimeOffset start, DateTimeOffset? end) =>
        new(Guid.NewGuid(), start, start.AddSeconds(15), end, IncidentClassification.ExternalConnectivityUnavailable,
            ClassificationConfidence.High, "The gateway responded but external targets did not.", ["ExternalIcmp 1.1.1.1"], ["Gateway 192.168.1.1"]);

    private static StoredObservation Obs(ProbeType type, string target, ProbeOutcome outcome, double? latency, DateTimeOffset at) =>
        new(0, Guid.NewGuid(), 1, null,
            new ProbeResult(type, target, "m", at, at.AddMilliseconds(5), outcome, latency, 1000, null, null));

    private sealed class FakeData : IObservationRepository, IIncidentRepository, IMonitoringGapRepository
    {
        private readonly List<StoredObservation> _observations = [];

        public List<Incident> Incidents { get; } = [];

        public List<MonitoringGap> Gaps { get; } = [];

        public List<(DateTimeOffset From, DateTimeOffset To)> ObservationQueries { get; } = [];

        public void Add(StoredObservation o) => _observations.Add(o);

        public Task<IReadOnlyList<StoredObservation>> GetObservationsAsync(
            DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken = default)
        {
            ObservationQueries.Add((fromUtc, toUtc));
            return Task.FromResult<IReadOnlyList<StoredObservation>>(
                _observations.Where(o => o.Result.StartedAtUtc >= fromUtc && o.Result.StartedAtUtc < toUtc).ToList());
        }

        public Task<IReadOnlyList<Incident>> GetOverlappingAsync(
            DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Incident>>(Incidents);

        Task<IReadOnlyList<MonitoringGap>> IMonitoringGapRepository.GetOverlappingAsync(
            DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MonitoringGap>>(Gaps);

        public Task SaveCycleAsync(MonitoringCycle cycle, bool recordNetworkSnapshot, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SaveAsync(Incident incident, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<int> CloseOrphanedAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<long> AddAsync(MonitoringGap gap, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task CloseAsync(long gapId, DateTimeOffset endedAtUtc, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
