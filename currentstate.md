# Current state

## 1. Project status

Initial scaffold (instructions.md section 18) complete locally. Phase 1 (Foundation) of
`docs/plan.md` is partially started: DI and logging are done, but the database, settings,
network discovery and probes are not.

## 2. Current version

0.1.0 (unreleased)

## 3. Current branch

`main`. Remote `origin` = `https://github.com/ranababu1/nettrace.git` (verified with `git remote -v`).

## 4. Implemented features

- Layered solution: App / Core / Infrastructure / Reporting plus three test projects.
- Generic host + DI in the WPF app. Clean start and exit, single instance per user session.
- Structured JSON-lines file logging to `%LOCALAPPDATA%\NetTrace\Logs\`.
- Per-user data folder layout (`AppDataPaths`).
- SHA-256 checksum compute/verify for exports.
- Shell window showing the *Unmonitored* state, the version and the data folder.

## 5. In-progress work

None.

## 6. Known issues and limitations

- No monitoring, persistence, tray, startup registration or exports yet. The UI says so.
- `docs/plan.md` and `docs/instructions.md` live in `docs/`, while instructions.md section 1
  says they belong at the repository root. They were left where they were provided. Decide
  whether to move them.
- The old template folder `nettrace/` and `nettrace.slnx` are still on disk (deleting them
  was not permitted in this session). They are excluded from git locally via
  `.git/info/exclude` and are not in `NetTrace.sln`. Delete them manually.
- The solution file is `NetTrace.sln` (classic format) to match instructions.md. The .NET 10
  default would be `.slnx`.
- Single-instance activation can be missed if a second launch happens in the brief window
  before the first instance has created its activation event. The second instance still exits.

## 7. Last successful build

2026-11-03: `dotnet build NetTrace.sln -c Release`, 0 warnings, 0 errors (.NET SDK 10.0.403).

## 8. Last test result

2026-11-03: `dotnet test NetTrace.sln -c Release`, 17 passed, 0 failed, 0 skipped.

## 9. Last commit/push

Commits are local on `main`. **Not pushed yet:** pushing to the public GitHub repo was
blocked pending user confirmation in this session.

## 10. Next recommended tasks

1. Push `main` to `origin` once approved.
2. SQLite schema and migrations (`Microsoft.Data.Sqlite`, or EF Core; decision needed per
   plan.md section 2) with `ApplicationEvents` and `AppSettings` tables, plus migration tests.
3. Settings persistence (`ISettingsRepository`) and `MonitoringConfiguration` with validation
   (intervals 5/10/15/30/60 s).
4. Network interface and default gateway discovery (`INetworkInterfaceProvider`) with fakes.
5. Gateway and external ICMP probes with timeouts, plus a non-overlapping scheduler.
6. Record application start/stop markers and reconcile unclean shutdowns as unknown gaps.
