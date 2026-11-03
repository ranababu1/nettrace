# NetTrace - Product & Technical Implementation Plan

**Project:** NetTrace\
**Version:** 1.0\
**Document:** Product and Technical Plan\
**Target platform:** Windows 10 and Windows 11, x64\
**Primary language:** C#\
**Desktop framework:** WPF on .NET 10 LTS\
**Storage:** SQLite\
**Operating model:** Local-first, tray-capable desktop application

------------------------------------------------------------------------

## 1. Product vision

NetTrace is a lightweight Windows network reliability monitor. It runs
in the background, measures local-network and internet connectivity,
records timestamped observations, detects and classifies network
incidents, and produces structured reports that users can share with
their ISP.

NetTrace is not primarily a speed-test application. Its main purpose is
continuous passive observation and reliable historical evidence.

### 1.1 Product goals

1.  Monitor network health continuously while the application is running
    and Windows is awake.
2.  Detect and timestamp connectivity degradation and outages.
3.  Distinguish local network problems from failures beyond the local
    gateway where evidence permits.
4.  Measure latency, packet loss, jitter, DNS resolution and HTTPS
    reachability.
5.  Record Wi-Fi and network-interface context.
6.  Store raw observations and day-wise summaries locally.
7.  Export selected periods as JSON and CSV.
8.  Make monitoring gaps explicit. Never misrepresent sleep, shutdown or
    application downtime as ISP downtime.
9.  Operate with low resource consumption and no cloud dependency.

### 1.2 Non-goals for v1

-   Continuous bandwidth/speed testing. Passive live throughput display
    (section 4.11) is in scope because it generates no test traffic.
-   ISP-specific integrations or automatic complaint submission.
-   Cloud synchronization or user accounts.
-   A Windows kernel driver.
-   A Windows Service in the first release.
-   Definitive attribution of every external failure to the ISP.
-   Network packet capture or inspection of user traffic.
-   VPN detection or deep traffic analysis, except where useful as
    contextual metadata.
-   PDF report generation in the MVP. Plan it as a later enhancement.

------------------------------------------------------------------------

## 2. Product decisions and defaults

  -----------------------------------------------------------------------
  Area                                Decision
  ----------------------------------- -----------------------------------
  Product name                        NetTrace

  Supported OS                        Windows 10 and Windows 11 x64

  Runtime                             .NET 10 LTS

  UI                                  WPF

  UI architecture                     MVVM

  Monitoring interval                 Configurable; 5 seconds default

  Storage                             SQLite

  ORM/data access                     EF Core with SQLite provider, or a
                                      measured lightweight repository
                                      implementation

  Retention                           Configurable; Forever default

  Wi-Fi diagnostics                   Enabled where hardware and OS
                                      expose data

  Background mode                     System tray

  Startup                             Optional, configurable

  Startup presentation                Open dashboard or start minimized

  Exports                             JSON and CSV

  Integrity                           SHA-256 manifest/checksum

  Cloud                               None required

  Installer                           Windows x64 installer

  Live throughput                     Passive adapter counters shown in
                                      tray; display-only, not persisted

  Speed test                          Manual, future phase
  -----------------------------------------------------------------------

The application must detect and clearly communicate unsupported or
unavailable measurements instead of fabricating values.

------------------------------------------------------------------------

## 3. User stories

### 3.1 Monitoring

-   As a user, I can start and stop monitoring.
-   As a user, I can configure the monitoring interval.
-   As a user, I can see whether monitoring is active.
-   As a user, I can see the current network interface, connection type,
    gateway and available Wi-Fi information.
-   As a user, I can continue monitoring while the dashboard is closed
    and the application remains in the system tray.
-   As a user, I can enable or disable launch-at-login.
-   As a user, I can choose whether the application opens visibly or
    minimized at startup.
-   As a user, I can see my current download and upload rate in the
    system tray without opening the dashboard.
-   As a user, I can enable or disable the live throughput display.
-   As a user, I can choose which internet connection (Wi-Fi, Ethernet,
    etc.) the live throughput display tracks.
-   As a user, I can choose whether the tray shows which connection is
    being tracked.

### 3.2 Diagnostics

-   As a user, I can see gateway reachability separately from external
    reachability.
-   As a user, I can see latency, packet loss and jitter.
-   As a user, I can see DNS and HTTPS probe outcomes.
-   As a user, I can distinguish local network disconnection, gateway
    failure, DNS failure, destination-specific failure and broader
    external connectivity loss.
-   As a user, I can see Wi-Fi SSID and signal strength when available.

### 3.3 History and reporting

-   As a user, I can browse daily network health.
-   As a user, I can inspect an incident timeline.
-   As a user, I can export one day or a custom date range.
-   As a user, I can export summary-only or detailed observations.
-   As a user, I can retain raw records indefinitely or configure a
    retention period.
-   As a user, I can see periods when monitoring was unavailable.

------------------------------------------------------------------------

## 4. Functional requirements

### 4.1 Application lifecycle

-   Start as a normal user-mode desktop application.
-   Support a single running instance.
-   Support minimize-to-tray and restore from tray.
-   Closing the main window should follow a configurable behaviour:
    minimize to tray or exit.
-   Provide an explicit Exit action in the tray menu.
-   Register/unregister startup using a documented Windows-supported
    per-user mechanism.
-   Startup registration must be reversible from Settings.
-   Do not require administrator rights for ordinary monitoring or
    per-user startup.
-   Do not silently install a Windows Service.

### 4.2 Monitoring scheduler

-   Default interval: 5 seconds.
-   Supported interval options: 5, 10, 15, 30 and 60 seconds, with
    validation.
-   Run probes asynchronously and avoid overlapping probe cycles.
-   Use cancellation tokens for shutdown and configuration changes.
-   Apply per-probe timeout values.
-   Record scheduled time, actual start and completion time where
    useful.
-   Record probe errors and skipped cycles.
-   Avoid unbounded queues and unbounded in-memory history.
-   Keep monitoring work independent from UI thread activity.

A monitoring cycle should collect the measurements configured for that
cycle. Some probes may have slower intervals than the main cycle: -
Gateway and external reachability: every configured cycle. - DNS:
default every 30 seconds. - HTTPS: default every 30 seconds. - Interface
and Wi-Fi state: event-driven where possible, plus periodic
reconciliation. - Public IP: optional and infrequent, default every 5
minutes. This requires an external lookup and must be clearly labelled
as such.

### 4.3 Probe strategy

Use independent destinations and protocols. Suggested defaults: -
Gateway: active default gateway discovered from the current
route/interface. - External ICMP: configurable targets such as 1.1.1.1
and 8.8.8.8. - DNS: configurable test hostname and resolver strategy.
Record which resolver was used. - HTTPS: configurable HTTPS endpoints
with short timeouts.

Do not assume that a single ICMP timeout proves an outage. Some
endpoints block or rate-limit ICMP.

#### Required observation fields

Each probe result should capture: - Probe type. - Target/hostname/IP. -
Start timestamp UTC. - Completion timestamp UTC. - Duration/latency in
milliseconds when applicable. - Success/failure/timeout/error state. -
Error code or sanitized error detail. - Timeout used. - Measurement
method and probe configuration identifier.

#### Connectivity interpretation

Examples: - Gateway succeeds, multiple external targets fail: possible
upstream/external connectivity issue. - Gateway fails and interface is
connected: possible local gateway/Wi-Fi/LAN issue. - Interface
disconnected: local network disconnected. - IP connectivity succeeds but
DNS fails: DNS resolution issue. - ICMP fails but HTTPS succeeds:
ICMP-specific or target-specific failure, not a confirmed outage. - One
endpoint fails while others succeed: destination-specific or partial
connectivity issue.

The classification engine must preserve the underlying probe evidence
and must not overstate root cause.

### 4.4 Latency, packet loss and jitter

-   Store individual probe outcomes.
-   Calculate latency summaries for selected time windows.
-   Calculate packet loss using the count of failed eligible probes
    divided by total eligible probes, with probe type and target
    identified.
-   Define jitter consistently. For v1, use the mean absolute difference
    between consecutive successful latency samples for the same target
    and probe type within the aggregation window.
-   Do not combine measurements from different targets or protocols into
    one unexplained metric.
-   Label missing values as null/not measured, not zero.

### 4.5 Outage detection

-   Use configurable consecutive-failure thresholds.
-   Recommended default: require at least 3 consecutive failed cycles
    for a confirmed incident, subject to corroboration from independent
    probes.
-   A single failed probe must not create a confirmed outage.
-   Track suspected incidents separately from confirmed incidents.
-   Record incident start, detection time, end/recovery time and
    duration.
-   Record which probes failed and which continued to work.
-   Close an incident only after configurable recovery checks succeed.
-   Merge adjacent failures into one incident when the recovery period
    is shorter than the configured stability window.
-   Store classification confidence and evidence, not just a label.
-   Permit ongoing incidents with a null end timestamp.

Suggested incident classifications: - `LocalInterfaceDisconnected` -
`GatewayUnreachable` - `InternetConnectivityDegraded` -
`ExternalConnectivityUnavailable` - `DnsResolutionFailure` -
`HttpsConnectivityFailure` - `DestinationSpecificFailure` -
`HighLatency` - `PacketLoss` - `PartialConnectivity` - `Unknown`

Avoid labelling an incident `ISPOutage` as a proven fact. Use wording
such as `ExternalConnectivityUnavailable` or `PossibleUpstreamIssue`.

### 4.6 Wi-Fi and interface diagnostics

Capture where available: - Interface name and description. - Interface
type. - Interface operational status. - IPv4/IPv6 addresses where
relevant. - Default gateway. - DNS server addresses. - SSID. - Wi-Fi
signal quality/strength. - Wi-Fi connection/disconnection changes.

Do not store Wi-Fi passwords, saved network credentials or unrelated
sensitive network data. SSID and IP information can be sensitive;
explain this in export settings and allow the user to exclude or redact
them.

Windows APIs may expose Wi-Fi details differently depending on adapter
and driver. Unsupported fields must be null with an availability reason.

### 4.7 Daily history and statistics

Daily statistics should include: - Total elapsed period. - Observed
monitoring duration. - Confirmed healthy duration. - Confirmed degraded
duration. - Confirmed outage duration. - Unmonitored/gap duration. -
Uptime percentage over observed time. - Average, median, p95 and maximum
latency where enough samples exist. - Packet loss by target/probe
type. - Average jitter. - DNS failure count. - HTTPS failure count. -
Number of incidents. - Longest incident. - Monitoring coverage
percentage.

Define uptime percentage as healthy observed time divided by total
classified observed time, excluding unmonitored gaps. Document the
formula in the UI and exports. Do not count unmonitored time as uptime
or downtime.

### 4.8 Data retention

-   Default: Forever.
-   User-selectable retention options should include 30, 90, 180, 365
    days and Forever.
-   Before deleting old raw observations, preserve required daily
    aggregates and incident records according to the retention policy.
-   Provide an explicit cleanup operation and show the cutoff date and
    estimated records affected.
-   Never silently delete data on an application upgrade.
-   Retention changes must be recorded in application logs.

### 4.9 Export

Formats: - JSON: complete structured report. - CSV: observations and
incident events in clearly defined separate files or a documented export
bundle.

Export scopes: - Single day. - Date range. - Full history. -
Incident-only report. - Summary-only. - Detailed raw observations.

JSON export should contain: - Schema version. - Application name and
version. - Export timestamp. - Requested period. - Timezone identifier
and UTC boundaries. - Monitoring configuration and methodology. -
Network context, subject to privacy settings. - Summary metrics. -
Incident list. - Monitoring gaps. - Probe/observation records when
detailed export is selected. - Data quality notes and
missing-measurement explanations. - Integrity metadata.

Use ISO 8601 timestamps. UTC timestamps are authoritative. Include local
timestamps and timezone identifier for readability.

#### Integrity

-   Generate a SHA-256 hash of the exact exported data file.
-   For a multi-file export, generate a manifest containing each
    filename, size and SHA-256 hash.
-   Explain that the checksum detects changes after export but does not
    prove that the source measurements are independently verified or
    tamper-proof.
-   Do not include secrets or credentials in exports.

### 4.10 Notifications

Configurable Windows notifications: - Confirmed outage. - Connectivity
restored. - Sustained high latency. - Sustained packet loss. -
Monitoring stopped or database write failure.

Prevent notification storms with cooldowns and incident-level
deduplication.

### 4.11 Live throughput display

Show the current download and upload rate of the active connection in
the system tray.

-   Source: OS network-interface byte counters (for example
    `GetIfEntry2` / `MIB_IF_ROW2` 64-bit counters or
    `System.Net.NetworkInformation` interface statistics). Do not
    generate any test traffic.
-   Sample interval: 1 second by default, configurable between 1 and 5
    seconds. Rate = byte-counter delta / elapsed time from the
    injectable clock.
-   Connection selection (Settings):
    -   Automatic (default): the active interface carrying the default
        route; follows the switch when Windows changes connection.
    -   Specific connection: the user picks from the detected adapters,
        listed by type and name (for example `Wi-Fi - Intel AX211`,
        `Ethernet - Realtek PCIe`). Show each adapter's current status
        (connected/disconnected).
    -   If the selected connection is disconnected or removed, show
        unavailable for it; do not silently switch to another adapter.
        Keep the selection so it resumes when the adapter returns.
    -   Exclude loopback. Handle adapter changes, counter resets and
        wrap-around without showing spikes or negative values.
-   Connection label: option to show the tracked connection type in the
    tray tooltip and menu (for example `Wi-Fi`, `Ethernet`). On by
    default. Not drawn into the tray icon, to save space.
-   Tray icon: when enabled, render a compact download rate (for
    example `4m`) into the tray icon; otherwise show the normal state
    icon.
-   Tray tooltip: download rate, upload rate, adapter name and
    monitoring state, for example `↓ 4m  ↑ 310k - Wi-Fi`.
-   Rate formatting (applies to tray icon, tooltip and menu):
    -   Whole numbers only; never show decimals. Round down.
    -   Use compact lowercase suffixes to save space: `m` for MB/s and
        `k` for KB/s. Do not show `MB/s`, `KB/s` or `/s`.
    -   1 MB/s or more: show `m` (for example `4m`).
    -   Under 1 MB/s: show `k` (for example `310k`).
    -   Above 0 but under 1 KB/s: show `1k`.
    -   Exactly 0: show `0k`.
    -   Never show a plain byte value; `k` is the smallest unit.
    -   Option to show bits per second instead, using the same rules
        and suffixes (`m` = Mbps, `k` = Kbps).
-   Unavailable counters must display as unavailable, never as zero.
-   Label the value as current usage, not available ISP bandwidth. A low
    value while idle is normal.
-   Display-only in v1: throughput samples are not persisted, exported
    or used in incident detection or classification.
-   Sampling must run off the UI thread and update the tray at most
    once per sample interval.

------------------------------------------------------------------------

## 5. Non-functional requirements

### 5.1 Performance

-   Low CPU and memory usage while idle and monitoring.
-   No synchronous network calls on the UI thread.
-   No blocking database calls on the UI thread.
-   Bounded concurrency and bounded in-memory buffers.
-   Batch database writes where practical.
-   Use SQLite indexes for timestamp, date, target and incident lookups.
-   Measure resource use during acceptance testing rather than claiming
    a fixed budget without measurement.

### 5.2 Reliability

-   Recover cleanly after application restart.
-   Use database transactions for related writes.
-   Handle database busy/locked conditions with bounded retry.
-   Use cancellation-aware shutdown.
-   Record application start, stop and unexpected termination/recovery
    markers.
-   Detect and report clock changes or large time discontinuities where
    possible.
-   Do not fabricate missing measurements after a crash.

### 5.3 Privacy and security

-   Local-first storage.
-   No telemetry or analytics transmission by default.
-   No packet payload capture.
-   No passwords or credentials stored.
-   Exports must allow exclusion/redaction of SSID, local IP, gateway
    and public IP.
-   Protect files using normal Windows user-profile permissions.
-   Validate export paths and avoid path traversal.
-   Do not log secrets or unnecessary personal data.

### 5.4 Accessibility and usability

-   Clear status labels and text in addition to colour.
-   Keyboard-accessible controls.
-   Scalable layouts and readable chart labels.
-   Explain technical classifications in plain language.
-   Distinguish measured, inferred, unavailable and unknown values.

------------------------------------------------------------------------

## 6. Proposed solution architecture

Use a layered solution with clear dependency direction.

``` text
NetTrace.sln
|
+-- src/
|   +-- NetTrace.App/                 WPF application, views, view models, tray
|   +-- NetTrace.Core/                Domain models, interfaces, rules
|   +-- NetTrace.Infrastructure/      SQLite, Windows APIs, probe implementations
|   +-- NetTrace.Reporting/           JSON/CSV export and integrity manifest
|
+-- tests/
    +-- NetTrace.Core.Tests/
    +-- NetTrace.Infrastructure.Tests/
    +-- NetTrace.Reporting.Tests/
    +-- NetTrace.App.Tests/           Only if UI testing is practical
```

### 6.1 Dependency rules

-   `Core` must not depend on WPF or infrastructure implementation
    details.
-   `Infrastructure` implements interfaces defined by Core.
-   `Reporting` consumes domain/query abstractions and must not own
    monitoring logic.
-   `App` composes dependencies and hosts the UI/background lifecycle.
-   Tests must exercise classification and aggregation without requiring
    live internet access.

### 6.2 Key services

Suggested abstractions: - `IMonitoringCoordinator` - `IProbeScheduler` -
`INetworkProbe` - `INetworkInterfaceProvider` -
`IWifiDiagnosticsProvider` - `IOutageDetectionService` -
`IStatisticsAggregator` - `IThroughputMonitor` -
`IObservationRepository` -
`IIncidentRepository` - `ISettingsRepository` - `IReportExporter` -
`IStartupRegistrationService` - `IClock`

Use an injectable clock for deterministic tests.

### 6.3 Suggested domain models

-   `NetworkObservation`
-   `ProbeResult`
-   `NetworkInterfaceSnapshot`
-   `WifiSnapshot`
-   `OutageIncident`
-   `MonitoringGap`
-   `DailyNetworkSummary`
-   `MonitoringConfiguration`
-   `ExportRequest`
-   `ExportManifest`

Prefer enums for known states and stable string values in serialized
output. Include schema versioning.

------------------------------------------------------------------------

## 7. SQLite data model

Use migrations and a versioned schema.

### 7.1 Tables

#### `NetworkObservations`

Suggested columns: - `Id` integer primary key. - `ObservedAtUtc`
text/ISO timestamp. - `CycleId` text or GUID. - `ProbeType` text. -
`Target` text. - `Success` integer/boolean. - `LatencyMs` real
nullable. - `DurationMs` real. - `TimedOut` integer/boolean. -
`ErrorCode` text nullable. - `ErrorMessage` text nullable and
sanitized. - `InterfaceId` text nullable. - `Classification` text
nullable. - `ConfigurationVersion` integer.

#### `NetworkSnapshots`

-   `Id`
-   `CapturedAtUtc`
-   `InterfaceName`
-   `InterfaceType`
-   `OperationalStatus`
-   `Ipv4AddressesJson`
-   `GatewayAddress`
-   `DnsServersJson`
-   `Ssid`
-   `WifiSignalQuality`
-   `MetadataJson`

#### `OutageEvents`

-   `Id`
-   `StartedAtUtc`
-   `DetectedAtUtc`
-   `EndedAtUtc` nullable
-   `DurationSeconds` nullable until closed
-   `Classification`
-   `Confidence`
-   `GatewayReachable`
-   `ExternalReachable`
-   `DnsSuccessful`
-   `HttpsSuccessful`
-   `EvidenceJson`
-   `IsConfirmed`

#### `MonitoringGaps`

-   `Id`
-   `StartedAtUtc`
-   `EndedAtUtc` nullable
-   `Reason`
-   `Details`
-   `Source`

#### `DailyStatistics`

-   `DateLocal`
-   `TimeZoneId`
-   `ObservedSeconds`
-   `HealthySeconds`
-   `DegradedSeconds`
-   `OutageSeconds`
-   `UnmonitoredSeconds`
-   `UptimePercentage`
-   `AverageLatencyMs`
-   `MedianLatencyMs`
-   `P95LatencyMs`
-   `MaximumLatencyMs`
-   `AverageJitterMs`
-   `PacketLossPercentage`
-   `IncidentCount`
-   `LongestIncidentSeconds`
-   `LastCalculatedAtUtc`

#### `ApplicationEvents`

-   `Id`
-   `OccurredAtUtc`
-   `EventType`
-   `DetailsJson`

#### `AppSettings`

-   `Key`
-   `Value`
-   `UpdatedAtUtc`

### 7.2 Indexes

At minimum: - `NetworkObservations(ObservedAtUtc)` -
`NetworkObservations(ProbeType, Target, ObservedAtUtc)` -
`OutageEvents(StartedAtUtc, EndedAtUtc)` -
`MonitoringGaps(StartedAtUtc, EndedAtUtc)` -
`DailyStatistics(DateLocal)`

### 7.3 Storage location

Use a per-user application data directory, for example:

`%LOCALAPPDATA%\\NetTrace\\`

Suggested contents: - `nettrace.db` - `Logs\\` - `Exports\\` -
`Backups\\` (if backup feature is implemented)

Do not store mutable application data beside the executable or
installer.

------------------------------------------------------------------------

## 8. UI and navigation

### 8.1 Main navigation

1.  Dashboard
2.  History
3.  Incidents
4.  Reports / Export
5.  Settings
6.  About

### 8.2 Dashboard

Display: - Current monitoring state. - Internet status and
classification. - Active interface and connection type. - Gateway
status. - External probe status. - Current/last latency. - Packet loss
and jitter. - Wi-Fi SSID and signal strength where available. - Today's
uptime and monitoring coverage. - Latency chart. - Connectivity
timeline. - Recent incidents and warnings.

### 8.3 History

-   Calendar or day list with health indicators.
-   7-day, 30-day and custom range filters.
-   Daily summaries.
-   Click a date to inspect measurements and incidents.
-   Show unmonitored periods distinctly.

### 8.4 Incident details

-   Start, detection and recovery timestamps.
-   Duration.
-   Classification and confidence.
-   Gateway/external/DNS/HTTPS probe evidence.
-   Related observations.
-   Clear note that classification is diagnostic evidence, not
    definitive ISP fault attribution.

### 8.5 Settings

Sections: - General: launch at login, start minimized, close-to-tray. -
Monitoring: enable/disable, interval, probe targets and timeouts. -
Diagnostics: gateway, external, DNS, HTTPS, Wi-Fi and optional public
IP. - Tray: live throughput display on/off, tray icon text on/off,
tracked connection (Automatic or a specific Wi-Fi/Ethernet/other
adapter), show connection label on/off, sample interval, bytes or bits
per second. - Alerts: notification toggles and thresholds. - Data: retention,
database location information, export defaults. - Privacy: fields to
redact from exports.

### 8.6 System tray

Menu: - Open NetTrace. - Monitoring: Start/Stop. - Current status. -
Current download/upload rate. - Export today's report. - Settings. -
Exit.

When live throughput display is enabled, the tray icon and tooltip show
the current rate as defined in section 4.11.

Tray icon and tooltip should reflect monitoring state without relying on
colour alone.

------------------------------------------------------------------------

## 9. Windows integration

### 9.1 Startup

Implement per-user launch-at-login registration using a documented
Windows mechanism, such as the current user's Run registry key or a
Startup-folder shortcut. Choose one mechanism, document it and test
install/uninstall behaviour.

Requirements: - No elevation for per-user startup. - Enable/disable from
Settings. - Do not create duplicate entries. - Start minimized when
configured. - Remove registration cleanly when disabled or
uninstalled. - Do not use Task Scheduler or a Windows Service unless a
later requirement justifies it.

### 9.2 Sleep, resume and shutdown

-   Subscribe to relevant Windows power/session events.
-   Mark monitoring unavailable during sleep/hibernate or application
    shutdown.
-   On resume, refresh interface and gateway discovery before
    classifying connectivity.
-   Do not count sleep duration as network downtime.
-   Record the reason for a monitoring gap when known.
-   If the process is terminated unexpectedly, reconcile the previous
    open monitoring session on next launch and mark the interval as an
    unknown gap, not a confirmed outage.

### 9.3 Network changes

Listen for network address/interface changes where practical. Also
perform periodic reconciliation to avoid relying exclusively on events.

------------------------------------------------------------------------

## 10. Export schema outline

JSON schema should be versioned. Example top-level structure:

``` json
{
  "schema_version": "1.0",
  "application": {
    "name": "NetTrace",
    "version": "1.0.0"
  },
  "report": {
    "generated_at_utc": "2026-11-03T13:00:00Z",
    "period_start_utc": "2026-11-03T00:00:00Z",
    "period_end_utc": "2026-11-04T00:00:00Z",
    "timezone_id": "India Standard Time"
  },
  "methodology": {
    "monitoring_interval_seconds": 5,
    "outage_confirmation_threshold": 3,
    "uptime_definition": "Healthy observed duration divided by classified observed duration. Unmonitored periods are excluded."
  },
  "network_context": {},
  "summary": {},
  "incidents": [],
  "monitoring_gaps": [],
  "observations": [],
  "data_quality_notes": [],
  "integrity": {
    "algorithm": "SHA-256",
    "data_file_sha256": "..."
  }
}
```

The example is illustrative. Actual exports must contain real collected
values, accurate units, nulls for unavailable measurements and no
fabricated sample data.

------------------------------------------------------------------------

## 11. Testing strategy

### 11.1 Unit tests

-   Outage confirmation and recovery thresholds.
-   Classification matrix.
-   Jitter calculation.
-   Throughput connection selection (Automatic vs specific adapter,
    disconnected selected adapter shows unavailable).
-   Throughput rate calculation and formatting (KB/MB thresholds,
    no decimals, 1 KB minimum for non-zero), including counter reset,
    wrap-around and adapter switch.
-   Packet loss calculation.
-   Uptime and monitoring coverage calculations.
-   Timezone/day-boundary handling.
-   Clock discontinuity handling.
-   Retention cutoff calculation.
-   Export serialization and schema version.
-   SHA-256 verification.

### 11.2 Integration tests

-   SQLite creation and migrations.
-   Transaction behaviour.
-   Repository queries and date-range filtering.
-   Export of large observation sets.
-   Startup registration enable/disable.
-   Network interface discovery.
-   Application restart and recovery.

### 11.3 Deterministic testability

Do not make core tests depend on live public endpoints. Use fake probes
and an injectable clock. Live-network smoke tests may be optional and
clearly separated.

### 11.4 Acceptance tests

-   Simulate gateway reachable/external unavailable.
-   Simulate gateway unavailable.
-   Simulate DNS failure with successful IP connectivity.
-   Simulate one failed endpoint while another succeeds.
-   Simulate latency spikes and packet loss.
-   Simulate sleep/resume and verify a monitoring gap is recorded.
-   Stop and restart the application.
-   Export a single day and a multi-day period.
-   Verify exported hashes.
-   Verify data retention does not remove data when set to Forever.
-   Verify startup option can be enabled and disabled.

------------------------------------------------------------------------

## 12. Delivery roadmap

### Phase 1: Foundation

-   Create solution and projects.
-   Configure dependency injection and logging.
-   Create SQLite database and migrations.
-   Implement settings persistence.
-   Implement network interface and gateway discovery.
-   Implement gateway and external probes.
-   Add monitoring lifecycle and application shutdown handling.

**Exit criteria:** observations are recorded locally and survive
restart.

### Phase 2: Diagnostics and incident engine

-   Add DNS and HTTPS probes.
-   Add Wi-Fi diagnostics.
-   Add latency, packet loss and jitter aggregation.
-   Implement outage detection and classification.
-   Implement monitoring-gap tracking.
-   Add automated tests for classification.

**Exit criteria:** simulated incidents are correctly classified without
relying on live internet tests.

### Phase 3: Dashboard and history

-   Build WPF shell and navigation.
-   Add dashboard status cards and charts.
-   Add incident timeline.
-   Add daily history and date-range summaries.
-   Add tray icon and notifications.
-   Add live throughput display in tray icon and tooltip.

**Exit criteria:** user can inspect current and historical network
quality.

### Phase 4: Reporting

-   Implement JSON export.
-   Implement CSV export.
-   Implement redaction options.
-   Add methodology and monitoring-gap information.
-   Add SHA-256 checksum and manifest.
-   Test large exports.

**Exit criteria:** reports are accurate, complete and independently
hash-verifiable.

### Phase 5: Packaging and hardening

-   Startup integration.
-   Installer and uninstaller.
-   Retention management.
-   Resource-use profiling.
-   Database backup/recovery strategy.
-   Accessibility and UX polish.
-   Release validation.

**Exit criteria:** installable Windows x64 release with documented
limitations and successful acceptance tests.

------------------------------------------------------------------------

## 13. Risks and mitigations

  -----------------------------------------------------------------------
  Risk                                Mitigation
  ----------------------------------- -----------------------------------
  ICMP blocked or rate-limited        Use multiple targets and TCP/HTTPS
                                      corroboration

  DNS resolver outage mistaken for    Test IP connectivity separately and
  internet outage                     record resolver

  Wi-Fi driver does not expose signal Mark unavailable; do not fabricate
  data                                

  PC sleeps or shuts down             Explicit monitoring-gap records

  Clock changes                       Store UTC and detect time
                                      discontinuities

  Large database over time            Indexed queries, retention controls
                                      and optional archival

  SQLite locked during export         Read transaction/snapshot strategy
                                      and bounded retry

  False outage alerts                 Consecutive failures, corroboration
                                      and recovery stability

  ISP fault cannot be proven          Use evidence-based classifications
                                      and cautious wording

  Sensitive network metadata in       Redaction controls and export
  export                              preview
  -----------------------------------------------------------------------

------------------------------------------------------------------------

## 14. Definition of done for v1.0

NetTrace v1.0 is ready when:

-   The solution builds in Release configuration.
-   Automated tests pass.
-   Monitoring works at the configured interval without overlapping
    cycles.
-   Gateway, external, DNS and HTTPS results are independently recorded.
-   Wi-Fi diagnostics are captured where available.
-   Incidents are detected and classified using tested rules.
-   Sleep, shutdown and application downtime are not counted as ISP
    downtime.
-   Raw observations and daily summaries persist in SQLite.
-   Retention defaults to Forever and can be changed.
-   JSON and CSV exports work for a day and a date range.
-   Export methodology, timezone, gaps and integrity data are included.
-   Startup and tray behaviour work as configured.
-   Live throughput in the tray matches adapter activity, handles
    adapter changes and generates no network traffic.
-   The installer and uninstaller are tested.
-   `changelog.md` and `currentstate.md` are current.
-   No secrets, database files, build output or user-specific local data
    are committed.

------------------------------------------------------------------------

## 15. Future enhancements

Potential post-v1 features: - Human-readable PDF ISP report. - Manual
speed test with server and timestamp metadata. - Router-specific
diagnostics where supported. - Optional traceroute/path diagnostics
during incidents. - IPv6-specific monitoring. - VPN/proxy state
annotation. - Export templates for ISP complaint submissions. - Optional
portable mode. - Optional encrypted backup/export. - Advanced charts and
long-term trend analysis.

These must not compromise the local-first, low-overhead monitoring
model.
