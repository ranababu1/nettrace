# Current state

## 1. Project status

The initial scaffold (instructions.md section 18) is complete. Phase 1 (Foundation) of
`docs/plan.md` is in progress: DI, logging, the SQLite schema/migrations, settings and
application lifecycle/gap tracking are done. Network discovery, probes and the monitoring
scheduler are not.

## 2. Current version

0.1.0 (unreleased)

## 3. Current branch

`main`. Remote `origin` = `https://github.com/ranababu1/nettrace.git` (verified with `git remote -v`).

## 4. Implemented features

- Layered solution: App / Core / Infrastructure / Reporting plus three test projects.
- Generic host + DI in the WPF app. Clean start and exit, single instance per user session.
- Structured JSON-lines file logging to `%LOCALAPPDATA%\NetTrace\Logs\`.
- Per-user data folder layout (`AppDataPaths`).
- SQLite database `%LOCALAPPDATA%\NetTrace\nettrace.db`: schema v1 (all plan.md section 7
  tables and indexes), forward-only migrations applied at startup, WAL, busy timeout plus
  bounded retry, and refusal of databases from newer app versions.
- Monitoring configuration with plan defaults and validation, persisted as versioned JSON
  in `AppSettings`.
- Lifecycle tracking: start/stop events, 30 s heartbeat, and startup reconciliation into
  `ApplicationNotRunning` / `UnexpectedTermination` monitoring gaps.
- SHA-256 checksum compute/verify for exports.
- Shell window showing the *Unmonitored* state, the version and the data folder.

## 5. In-progress work

None.

## 6. Known issues and limitations

- No monitoring, repositories, tray, startup registration or exports yet. The UI says so.
- Decision taken: data access uses `Microsoft.Data.Sqlite` with hand-written SQL migrations
  rather than EF Core (plan.md section 2 allowed either). It is lighter and gives direct
  control over batched observation writes.
- `docs/plan.md` and `docs/instructions.md` stay in `docs/`, where they were provided, even
  though instructions.md section 1 says they belong at the repository root.
- The old template folder `nettrace/` and `nettrace.slnx` are still on disk (deleting them
  was not permitted). They are excluded from git locally via `.git/info/exclude` and are not
  in `NetTrace.sln`. Delete them manually.
- The solution file is `NetTrace.sln` (classic format) to match instructions.md.
- Single-instance activation can be missed if a second launch happens in the brief window
  before the first instance has created its activation event. The second instance still exits.

## 7. Last successful build

2026-11-03: `dotnet build NetTrace.sln -c Release`, 0 warnings, 0 errors (.NET SDK 10.0.403).

## 8. Last test result

2026-11-03: `dotnet test NetTrace.sln -c Release`, 82 passed, 0 failed, 0 skipped.

## 9. Last commit/push

`main` is pushed to `origin` after each increment. History was rewritten once on 2026-11-03
(authorized) to remove commit-message trailers; trees were unchanged.

## 10. Next recommended tasks

1. Network interface and default gateway discovery (`INetworkInterfaceProvider`) with fakes,
   refreshed on network-change events plus periodic reconciliation.
2. Gateway and external ICMP probes with timeouts, plus a non-overlapping scheduler.
3. Observation repository with batched writes.
4. Sleep/resume handling: record `SystemSleep` gaps and refresh network context on resume.
5. Known limitation to revisit: the gap after a crash starts at the last heartbeat (at most
   30 s before the crash). Once observations exist, use the latest observation if it is newer.
