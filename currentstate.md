# Current state

## 1. Project status

The initial scaffold is complete. Phase 1 (Foundation) of `docs/plan.md` has reached its
exit criterion: observations are recorded locally while the app runs and survive restart.
Gateway and external ICMP monitoring runs in the background. DNS/HTTPS probes, Wi-Fi details,
incident detection (Phase 2) and the dashboard/tray UI (Phase 3) are not started.

## 2. Current version

0.1.0 (unreleased)

## 3. Current branch

`main`. Remote `origin` = `https://github.com/ranababu1/nettrace.git` (verified with `git remote -v`).

## 4. Implemented features

- Layered solution: App / Core / Infrastructure / Reporting plus three test projects.
- Generic host + DI in the WPF app. Clean start and exit, single instance per user session.
- Structured JSON-lines file logging to `%LOCALAPPDATA%\NetTrace\Logs\`.
- SQLite database `%LOCALAPPDATA%\NetTrace\nettrace.db`: schema v2, forward-only migrations
  at startup, WAL, busy timeout plus bounded retry, and refusal of newer-version databases.
- Monitoring configuration with plan defaults and validation, persisted as versioned JSON.
- Lifecycle tracking: start/stop events, 30 s heartbeat, and startup reconciliation into
  `ApplicationNotRunning` / `UnexpectedTermination` gaps.
- Network interface and default-gateway discovery with change notifications.
- Background monitoring: every interval (5 s default), gateway plus `1.1.1.1` / `8.8.8.8`
  ICMP. Fixed-rate schedule with no overlap. Missed cycles become `Unknown` gaps and disabled
  monitoring is a `MonitoringDisabled` gap. Observations and network snapshots (on change)
  are persisted.
- SHA-256 checksum compute/verify for exports.
- Main window shows live monitoring state (with glyph and text), active connection, default
  gateway, last successful check, and the latest result for each check, updated after every
  cycle. It shows no health verdict yet (classification is Phase 2).

## 5. In-progress work

None.

## 6. Known issues and limitations

- No tray, startup registration, DNS/HTTPS probes, incident classification, history,
  settings UI or export yet. Closing the window exits the app.
- Configuration changes take effect only after a restart (no settings UI yet).
- Windows ICMP reports round-trip time in whole milliseconds. Sub-millisecond replies (typical
  for a wired gateway) are stored as `0` ms: a real measurement, not a missing value.
- Sleep is not yet labelled as `SystemSleep`. A sleep shows up as an `Unknown` gap of missed
  cycles, which is accurate but less specific.
- The gap after a crash starts at the last heartbeat (at most 30 s before the crash).
- Decision taken: `Microsoft.Data.Sqlite` with hand-written SQL migrations rather than EF Core.
- `docs/plan.md` and `docs/instructions.md` stay in `docs/`, where they were provided.
- The old template folder `nettrace/` and `nettrace.slnx` are still on disk, excluded from git
  locally via `.git/info/exclude`. Delete them manually.
- Single-instance activation can be missed if a second launch happens in the brief window
  before the first instance has created its activation event. The second instance still exits.

## 7. Last successful build

2026-11-03: `dotnet build NetTrace.sln -c Release`, 0 warnings, 0 errors (.NET SDK 10.0.403).

## 8. Last test result

2026-11-03: `dotnet test NetTrace.sln -c Release`, 131 passed, 0 failed, 0 skipped.

## 9. Last commit/push

`main` is pushed to `origin` after each increment. History was rewritten once on 2026-11-03
(authorized) to remove commit-message trailers; trees were unchanged.

## 10. Next recommended tasks

1. Sleep/resume: subscribe to power events, record `SystemSleep` gaps, and refresh network
   context on resume.
2. Phase 2: DNS and HTTPS probes on their 30 s cadence.
3. Phase 2: outage detection and classification (consecutive-failure thresholds,
   corroboration, recovery, merge window) with deterministic tests.
4. Use the latest observation to tighten the start of crash gaps.
