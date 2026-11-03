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
- Fluent (Windows 11, light/dark) main window: status card, live Download/Upload/Router/
  Internet tiles, latest checks. No health verdict yet (classification is Phase 2).
- System tray with app icon, live download rate in the icon, both rates plus connection
  and state in the tooltip, and an Open/Exit menu. Closing the window hides to the tray.
- Live throughput from OS adapter counters (Automatic connection; display-only).

## 5. In-progress work

None.

## 6. Known issues and limitations

- No startup registration, DNS/HTTPS probes, incident classification, history, settings UI
  or export yet. The tray menu therefore has no Start/Stop, Export today or Settings items.
- Throughput settings (enable, specific adapter, bits/bytes, icon text, label, interval) use
  defaults until the settings UI exists; the logic is implemented and tested.
- Windows 11 places new tray icons in the hidden overflow. Users must pin NetTrace via
  Settings > Personalization > Taskbar > Other system tray icons. (On the developer machine it
  was promoted via `HKCU\Control Panel\NotifyIconSettings\<id>\IsPromoted = 1`.)
- The tray icon is 16 px at 100% scaling, so 4-character rates such as `310k` render small.
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

2026-11-04: `dotnet test NetTrace.sln -c Release`, 175 passed, 0 failed, 0 skipped.

## 9. Last commit/push

`main` is pushed to `origin` after each increment. History was rewritten once on 2026-11-03
(authorized) to remove commit-message trailers; trees were unchanged.

## 10. Next recommended tasks

1. Phase 2: outage detection and classification (consecutive-failure thresholds,
   corroboration, recovery, merge window) with deterministic tests.
2. Use the latest observation to tighten the start of crash gaps.
