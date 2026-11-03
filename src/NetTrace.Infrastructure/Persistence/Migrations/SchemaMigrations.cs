namespace NetTrace.Infrastructure.Persistence.Migrations;

/// <summary>
/// The ordered list of schema migrations. Append new migrations; never modify or remove an
/// existing one, and never drop historical data in a migration.
/// </summary>
/// <remarks>
/// Conventions: timestamps are ISO 8601 UTC text (see <see cref="SqliteTimestamp"/>), booleans are
/// INTEGER 0/1, and unavailable measurements are NULL, never zero.
/// </remarks>
public static class SchemaMigrations
{
    public static IReadOnlyList<SchemaMigration> All { get; } =
    [
        new(1, "InitialSchema", InitialSchemaSql),
    ];

    public static int LatestVersion => All[^1].Version;

    private const string InitialSchemaSql = """
        CREATE TABLE NetworkObservations (
            Id                   INTEGER PRIMARY KEY,
            ObservedAtUtc        TEXT    NOT NULL,
            CompletedAtUtc       TEXT    NULL,
            CycleId              TEXT    NOT NULL,
            ProbeType            TEXT    NOT NULL,
            Target               TEXT    NOT NULL,
            Method               TEXT    NULL,
            Success              INTEGER NOT NULL CHECK (Success IN (0, 1)),
            TimedOut             INTEGER NOT NULL CHECK (TimedOut IN (0, 1)),
            LatencyMs            REAL    NULL,
            DurationMs           REAL    NULL,
            TimeoutMs            INTEGER NOT NULL,
            ErrorCode            TEXT    NULL,
            ErrorMessage         TEXT    NULL,
            InterfaceId          TEXT    NULL,
            Classification       TEXT    NULL,
            ConfigurationVersion INTEGER NOT NULL
        );
        CREATE INDEX IX_NetworkObservations_ObservedAtUtc
            ON NetworkObservations (ObservedAtUtc);
        CREATE INDEX IX_NetworkObservations_ProbeType_Target_ObservedAtUtc
            ON NetworkObservations (ProbeType, Target, ObservedAtUtc);

        CREATE TABLE NetworkSnapshots (
            Id                  INTEGER PRIMARY KEY,
            CapturedAtUtc       TEXT    NOT NULL,
            InterfaceId         TEXT    NULL,
            InterfaceName       TEXT    NULL,
            InterfaceType       TEXT    NULL,
            OperationalStatus   TEXT    NULL,
            Ipv4AddressesJson   TEXT    NULL,
            GatewayAddress      TEXT    NULL,
            DnsServersJson      TEXT    NULL,
            Ssid                TEXT    NULL,
            WifiSignalQuality   INTEGER NULL CHECK (WifiSignalQuality BETWEEN 0 AND 100),
            MetadataJson        TEXT    NULL
        );
        CREATE INDEX IX_NetworkSnapshots_CapturedAtUtc
            ON NetworkSnapshots (CapturedAtUtc);

        CREATE TABLE OutageEvents (
            Id                INTEGER PRIMARY KEY,
            StartedAtUtc      TEXT    NOT NULL,
            DetectedAtUtc     TEXT    NOT NULL,
            EndedAtUtc        TEXT    NULL,
            DurationSeconds   REAL    NULL,
            Classification    TEXT    NOT NULL,
            Confidence        TEXT    NOT NULL,
            GatewayReachable  INTEGER NULL CHECK (GatewayReachable IN (0, 1)),
            ExternalReachable INTEGER NULL CHECK (ExternalReachable IN (0, 1)),
            DnsSuccessful     INTEGER NULL CHECK (DnsSuccessful IN (0, 1)),
            HttpsSuccessful   INTEGER NULL CHECK (HttpsSuccessful IN (0, 1)),
            EvidenceJson      TEXT    NOT NULL,
            IsConfirmed       INTEGER NOT NULL CHECK (IsConfirmed IN (0, 1))
        );
        CREATE INDEX IX_OutageEvents_StartedAtUtc_EndedAtUtc
            ON OutageEvents (StartedAtUtc, EndedAtUtc);

        CREATE TABLE MonitoringGaps (
            Id           INTEGER PRIMARY KEY,
            StartedAtUtc TEXT NOT NULL,
            EndedAtUtc   TEXT NULL,
            Reason       TEXT NOT NULL,
            Details      TEXT NULL,
            Source       TEXT NOT NULL
        );
        CREATE INDEX IX_MonitoringGaps_StartedAtUtc_EndedAtUtc
            ON MonitoringGaps (StartedAtUtc, EndedAtUtc);

        -- The primary key's leading DateLocal column serves DailyStatistics(DateLocal) lookups.
        CREATE TABLE DailyStatistics (
            DateLocal              TEXT    NOT NULL,
            TimeZoneId             TEXT    NOT NULL,
            ObservedSeconds        REAL    NOT NULL,
            HealthySeconds         REAL    NOT NULL,
            DegradedSeconds        REAL    NOT NULL,
            OutageSeconds          REAL    NOT NULL,
            UnmonitoredSeconds     REAL    NOT NULL,
            UptimePercentage       REAL    NULL,
            AverageLatencyMs       REAL    NULL,
            MedianLatencyMs        REAL    NULL,
            P95LatencyMs           REAL    NULL,
            MaximumLatencyMs       REAL    NULL,
            AverageJitterMs        REAL    NULL,
            PacketLossPercentage   REAL    NULL,
            IncidentCount          INTEGER NOT NULL,
            LongestIncidentSeconds REAL    NULL,
            LastCalculatedAtUtc    TEXT    NOT NULL,
            PRIMARY KEY (DateLocal, TimeZoneId)
        ) WITHOUT ROWID;

        CREATE TABLE ApplicationEvents (
            Id            INTEGER PRIMARY KEY,
            OccurredAtUtc TEXT NOT NULL,
            EventType     TEXT NOT NULL,
            DetailsJson   TEXT NULL
        );
        CREATE INDEX IX_ApplicationEvents_OccurredAtUtc
            ON ApplicationEvents (OccurredAtUtc);

        CREATE TABLE AppSettings (
            Key          TEXT NOT NULL PRIMARY KEY,
            Value        TEXT NULL,
            UpdatedAtUtc TEXT NOT NULL
        ) WITHOUT ROWID;
        """;
}
