# Changelog

All notable changes to NetTrace. Newest entries first.

## Unreleased (0.1.0) - 2026-11-03

### Added

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

### Changed

- README rewritten as a user-facing product description.

### Removed

- The Visual Studio template project `nettrace/` was replaced by `src/NetTrace.App` and is no
  longer part of the solution (the folder remains on disk, untracked).

### Tests/validation

- 82 tests pass (`dotnet test NetTrace.sln -c Release`), including configuration validation,
  settings round-trip and fallback, lifecycle reconciliation rules, and lifecycle integration
  (clean stop, crash, unreadable session). Earlier coverage: Core dependency rule, data-path
  layout and validation, logger output/levels/rollover/disposal, SHA-256 known vector and
  tamper detection, migrations (fresh database, idempotence with data preserved, pending-only
  application, rollback on failure, newer-version refusal, WAL, sequence validation), retry
  behaviour, and the timestamp format.
- Manual smoke test: the first launch applied migration 1, and the second reported the schema
  current at version 1. Clean stop followed by a restart recorded an `ApplicationNotRunning`
  gap. A forced kill followed by a restart recorded an `UnexpectedTermination` gap and an
  unclean-shutdown warning.
- Manual smoke test: the app starts, a second launch exits with code 0, closing the window
  exits with code 0, and start/stop entries are written to the log.
