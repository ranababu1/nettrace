# NetTrace

NetTrace is a local-first Windows network reliability monitor. It is intended to run in the
background, record timestamped connectivity observations, classify incidents cautiously and
export evidence (JSON/CSV with SHA-256 checksums) that you can share with your ISP.

> **Status:** early scaffold (v0.1.0). The application shell, dependency injection, structured
> logging and data-folder layout exist. Network monitoring is **not** implemented yet, and the
> app reports its state as *Unmonitored*. See [`currentstate.md`](currentstate.md).

Product scope and architecture: [`docs/plan.md`](docs/plan.md). Engineering rules:
[`docs/instructions.md`](docs/instructions.md).

## Requirements

- Windows 10 or 11, x64
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (10.0.100 or later)

## Build, test and run

```powershell
dotnet restore NetTrace.sln
dotnet build NetTrace.sln -c Release
dotnet test NetTrace.sln -c Release
dotnet run --project src/NetTrace.App -c Release
```

You can also open `NetTrace.sln` in Visual Studio 2026 and run `NetTrace.App`.

Only one instance runs per Windows user session. Launching it again brings the running
window to the front.

## Solution layout

| Project | Purpose |
| --- | --- |
| `src/NetTrace.Core` | Domain types, rules and interfaces. No WPF, SQLite or network implementation dependencies (enforced by a test). |
| `src/NetTrace.Infrastructure` | Clock, data paths and the file logger. SQLite, Windows APIs and network probes will go here. |
| `src/NetTrace.Reporting` | Export and integrity functions (SHA-256 checksums so far). |
| `src/NetTrace.App` | WPF application: generic host, DI, view models and window lifecycle. |
| `tests/*` | xUnit tests. They need no internet access. |

## Data location

All mutable data lives under `%LOCALAPPDATA%\NetTrace\`, never beside the executable:

| Path | Contents |
| --- | --- |
| `nettrace.db` | SQLite database (not created yet) |
| `Logs\nettrace-yyyyMMdd.jsonl` | Structured application log, one JSON object per line, one file per UTC day |
| `Exports\` | User exports |

NetTrace sends no telemetry and has no cloud dependency. The data folder can contain
sensitive network metadata, so be careful before sharing it.

### Log format

Each log line is a JSON object with `timestamp_utc` (ISO 8601, UTC), `level`, `category`,
optional `event_id`/`event_name`, `message`, an optional `properties` object holding the
structured message values, and an optional `exception` object. The logger queues at most
4096 entries in memory. If the queue fills, extra entries are counted and a warning is
written in their place, so memory use stays bounded.

## Troubleshooting

- **Nothing happens when I start NetTrace:** another instance may already be running in your
  session. It should come to the front. Check Task Manager for `NetTrace.App.exe`.
- **Startup error dialog:** check the newest file in `%LOCALAPPDATA%\NetTrace\Logs\`.
