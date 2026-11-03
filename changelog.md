# Changelog

All notable changes to NetTrace. Newest entries first.

## Unreleased (0.1.0) - 2026-11-03

### Added

- Outage detection engine in Core (`Incidents`): a per-cycle classifier (local interface,
  gateway, external connectivity, DNS, HTTPS) that needs independent probes to agree, and an
  `IncidentTracker` with a confirmation threshold, recovery threshold, merge window and gap
  handling. `MonitoringService` feeds every cycle and gap to it, saves confirmed incidents to
  `OutageEvents` (migration 3 adds `IncidentId`), ends incidents orphaned by a crash at the last
  recorded observation, and publishes the open incident to the main window.
- DNS probe (system resolver) and HTTPS probe (GET, time to response headers, no redirects) on
  their own 30 s cadence. They run beside the main cycle without delaying it, and each result is
  stored by the first cycle that sees it finish, so slow timeouts cannot cause false missed-cycle gaps.
- Sleep/resume handling: `IPowerEvents` with a `SystemEvents`-based implementation. Cycles missed
  across an observed suspend are recorded as `SystemSleep` gaps, and the network context is
  refreshed on resume.
- `NetTrace.sln` with `NetTrace.App` (WPF, `net10.0-windows`), `NetTrace.Core` (`net10.0`),
  `NetTrace.Infrastructure` (`net10.0-windows`), `NetTrace.Reporting` (`net10.0`) and matching
  xUnit test projects. Project references follow the dependency direction in `docs/plan.md`.
- Shared build settings in `Directory.Build.props`: nullable, implicit usings, warnings as
  errors, version 0.1.0.
- `IClock` in Core with `SystemClock` in Infrastructure.
- `AppDataPaths` for per-user data under `%LOCALAPPDATA%\NetTrace\` (database, `Logs\`, `Exports\`).
- Structured JSON-lines file logger (`JsonFileLoggerProvider`): bounded queue, background
  writer, one file per UTC day, dropped-entry reporting. No third-party logging dependency.
- `Sha256Checksum` in Reporting to compute and verify export checksums.
- WPF shell: generic host with DI, file and debug logging, logged start/stop, logging of
  unhandled exceptions, a per-session single-instance guard that activates the running
  window, the built-in Fluent theme, and a main window showing the *Unmonitored* state, the
  version and the data folder, with Open data folder and Exit actions.
- `.gitignore`, `.gitattributes`, `README.md`, `changelog.md`, `currentstate.md`.
- SQLite database at `%LOCALAPPDATA%\NetTrace\nettrace.db` (`Microsoft.Data.Sqlite`), schema
  v1 with the tables and indexes from plan.md section 7: NetworkObservations, NetworkSnapshots,
  OutageEvents, MonitoringGaps, DailyStatistics, ApplicationEvents and AppSettings.
  NetworkObservations also stores `CompletedAtUtc`, `Method` and `TimeoutMs`, as plan.md
  section 4.3 requires.
- Forward-only migrations recorded in `SchemaMigrations`. Each one runs in its own
  transaction at host startup. A database from a newer app version is refused and left
  unchanged.
- WAL journal mode, a 5 s SQLite busy timeout, and a bounded retry (3 attempts) for
  busy/locked errors.
- Timestamps are stored as fixed-width ISO 8601 UTC text, so text order equals time order.

- `MonitoringConfiguration` with the plan defaults: 5 s interval; gateway plus `1.1.1.1` and
  `8.8.8.8` ICMP; DNS (`example.com`) and HTTPS (`google.com/generate_204`,
  `cloudflare.com/cdn-cgi/trace`) every 30 s; 3-cycle outage confirmation and 3-cycle
  recovery; 60 s merge window; public IP lookup **off** by default. A validator enforces the
  allowed intervals, timeouts shorter than their interval, IP-literal ICMP targets, https-only
  endpoints without credentials, and confirmation thresholds of at least 2.
- Settings persistence: `ISettingsRepository` over `AppSettings` with transactional
  multi-key writes. `MonitoringConfigurationStore` keeps the configuration as snake_case JSON
  with a version that increments on every save. Missing fields take their defaults. Unreadable
  or invalid stored settings fall back to defaults with a warning and are not overwritten.
- Application lifecycle: `ApplicationStarted`/`ApplicationStopped` events, a 30 s heartbeat,
  and startup reconciliation. Time since a clean stop is recorded as an `ApplicationNotRunning`
  monitoring gap. After a crash or kill, the gap is `UnexpectedTermination`, starting at the
  last heartbeat. Neither is ever treated as an outage. If the clock moved backwards, a
  `ClockDiscontinuityDetected` event is recorded instead of a gap.

- Network interface discovery (`INetworkInterfaceProvider`): adapters with kind, status,
  IPv4/IPv6 addresses, gateways and DNS servers. The active interface is the one Windows
  routes internet traffic through (`GetBestInterface`, which reads the routing table and sends
  no traffic). If that interface isn't eligible, the fallback order is Ethernet, Wi-Fi,
  cellular. Loopback, tunnel, down and gateway-less adapters are never active. Windows
  address/availability changes raise `NetworkChanged`.

- ICMP probe (`PingProbe`): 32-byte echo with the configured timeout, a hard deadline 1 s
  past the timeout, outcomes Success/Failure/Timeout/Error, and fixed error descriptions
  (raw exception text is never stored).
- Monitoring cycle runner: the gateway and every external ICMP target, probed concurrently.
  With no default gateway, the gateway probe is recorded as `Skipped`
  (`NoActiveConnection`), not as failed. A probe that throws unexpectedly is recorded as an
  error, and the rest of the cycle is kept.
- Fixed-rate cycle schedule. Cycles never overlap and missed slots are not run late in a
  burst: they are counted and recorded as an `Unknown` monitoring gap ("system may have been
  asleep or unresponsive"). If the clock moves backwards, the schedule resets.
- Background `MonitoringService`: loads the saved configuration and re-reads the network
  context on change events and at least every 60 s. Each cycle is saved in one transaction,
  with a network snapshot whenever the active connection, gateway, addresses or DNS servers
  change. Disabled monitoring is recorded as an open `MonitoringDisabled` gap, closed on stop.
  A failed database write is logged and monitoring continues. Status (state, last cycle,
  last successful measurement, last error) is exposed for the UI.
- Observation, network-snapshot and gap repositories (`SqliteMonitoringRepository`).
- Schema migration 2: `NetworkObservations.Outcome`, so Failure, Error and Skipped are kept
  distinct. Rows from before v2 are read back from `Success`/`TimedOut`.
- Live throughput (plan.md section 4.11): `ThroughputMonitor` samples the tracked adapter's
  OS byte counters every second. It sends no traffic and nothing is persisted. Automatic mode
  follows the active connection; a specific adapter is never silently replaced and shows
  unavailable while disconnected. A counter reset, wrap or adapter switch shows unavailable,
  never a spike or a negative value.
- Rate format: whole numbers rounded down, `m`/`k` suffixes in decimal units, `1k` for any
  non-zero rate under 1 KB/s, `0k` for exactly zero, `--` when unavailable; optional bits/s.
- System tray icon (`TrayIconService`, Windows Forms `NotifyIcon`). The icon text is the live
  download rate, drawn in black or white to match the taskbar theme, and falls back to the
  app icon when the rate is unavailable. The tooltip shows `↓ down  ↑ up - connection` and the
  monitoring state. The menu has Open NetTrace, status, current usage and Exit.
- App icon (`Assets/NetTrace.ico`, multi-size, regenerated by `tools/make-icon.ps1`) used for
  the executable, the window and the header.

### Changed

- Main window redesigned with the WPF Fluent theme (`ThemeMode="System"`, follows Windows
  light/dark): header with the app icon, a status card with connection and last-reply chips,
  live Download/Upload/Router/Internet tiles with explanatory tooltips, a latest-checks
  list, and a compact footer. Every status has a text label as well as an icon, never colour alone.
- Closing the window now hides it to the tray (a one-time notice explains this); monitoring
  continues. Exit (tray menu or window) quits.

- README rewritten as a user-facing product description.

### Removed

- The Visual Studio template project `nettrace/` was replaced by `src/NetTrace.App` and is no
  longer part of the solution (the folder remains on disk, untracked).

### Tests/validation

- 164 tests pass (rate formatting, throughput calculation and selection, sampler; `dotnet test NetTrace.sln -c Release`), including ping status mapping and
  loopback ping, cycle runner (skip, error isolation, cancellation), schedule (overrun, stall,
  clock reset), repository round-trips of every outcome, and the monitoring service loop
  (schedule, snapshots on change, missed-slot gaps, disabled gap, save failure), plus active-interface selection,
  adapter type/status mapping, a live local discovery check (no internet needed), configuration validation,
  settings round-trip and fallback, lifecycle reconciliation rules, and lifecycle integration
  (clean stop, crash, unreadable session). Earlier coverage: Core dependency rule, data-path
  layout and validation, logger output/levels/rollover/disposal, SHA-256 known vector and
  tamper detection, migrations (fresh database, idempotence with data preserved, pending-only
  application, rollback on failure, newer-version refusal, WAL, sequence validation), retry
  behaviour, and the timestamp format.
- Manual smoke test: the first launch applied migration 1, and the second reported the schema
  current at version 1. Clean stop followed by a restart recorded an `ApplicationNotRunning`
  gap. A forced kill followed by a restart recorded an `UnexpectedTermination` gap and an
  unclean-shutdown warning. A 22 s run on Ethernet recorded 5 cycles exactly 5 s apart
  (gateway plus 2 external targets each, all successful, external average 10.6 ms) and one
  network snapshot. Migration 2 was applied to the existing database.
- Manual smoke test: the app starts, a second launch exits with code 0, closing the window
  exits with code 0, and start/stop entries are written to the log.
