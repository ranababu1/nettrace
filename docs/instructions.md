# NetTrace - AI Coding Agent Instructions

**Project:** NetTrace\
**Target:** Windows 10/11 x64\
**Stack:** C#, .NET 10 LTS, WPF, SQLite\
**Primary objective:** Build a reliable local-first network reliability
monitor and evidence exporter.

This document is mandatory guidance for any AI coding agent or developer
working in this repository. Read `plan.md` before implementing features.

------------------------------------------------------------------------

## 1. Source of truth and project documents

Maintain these files at the repository root:

-   `plan.md`: product scope, architecture, requirements, data model,
    roadmap and acceptance criteria.
-   `instructions.md`: these engineering and agent instructions.
-   `changelog.md`: user-visible and technical changes, grouped by
    version/date.
-   `currentstate.md`: actual implementation status, current branch,
    build/test state, known issues, next actions and outstanding
    decisions.
-   `README.md`: setup, build, run, configuration, data location and
    troubleshooting.

Create `changelog.md` and `currentstate.md` at project initialization if
they do not exist. Update them as work progresses.

Do not silently change product scope or architecture. If a requirement
conflicts with `plan.md`, explain the conflict and ask before making a
material scope change.

## 2. Mandatory working principles

1.  Understand the existing repository before editing.
2.  Preserve user changes and uncommitted work.
3.  Work on the intended branch. Never change branches without
    authorization.
4.  Prefer small, buildable increments.
5.  Keep the solution compiling.
6.  Write tests for domain logic and bug fixes.
7.  Do not invent successful test results.
8.  Do not delete user data, databases, exports or configuration.
9.  Do not introduce cloud services, telemetry or external dependencies
    without approval.
10. Do not commit secrets, credentials, local databases, exports, logs
    or generated build output.
11. Explain important design choices and trade-offs.
12. Deliver complete, integrated code changes. Do not leave placeholder
    implementations for required functionality.

## 3. Git and push policy

### Repository

-   The only remote for this project is
    `https://github.com/ranababu1/nettrace.git`, configured as `origin`.
-   Push all code to this repository. Do not push to any other remote,
    fork or mirror.
-   If the local folder is not yet a Git repository, initialize it and
    add this remote as `origin`.
-   Before the first push in a session, verify with `git remote -v`
    that `origin` points to this URL. If it points elsewhere, stop and
    ask; do not change it silently.

### Push cadence

Push frequently. Do not let completed, buildable work sit unpushed.
Follow this strict push cadence:

-   Push after every file change, or
-   At minimum, push when approximately 50 lines of production code have
    changed since the previous push,
-   Whichever comes first.
-   Never end a work session or hand off a task with buildable work
    left unpushed.
-   Do not delay a small, complete requirement merely because it is
    under 50 lines.
-   If a change temporarily makes the solution non-building, still
    push it. Then complete the change, restore a successful build, run relevant tests, then push again.
-   Update `changelog.md` and `currentstate.md` before committing when
    feasible.
-   Commit and push only to the intended branch.
-   Do not amend, force-push, rebase shared history or rewrite commits
    unless explicitly authorized.
-   Do not discard or overwrite uncommitted user changes.
-   If remote access or credentials prevent a push, report that clearly.
    Do not claim the push succeeded.

Before committing: 1. Inspect `git status`. 2. Review the diff. 3.
Ensure no secrets or local data are staged. 4. Build the solution, if its in buildable state. 5.
Run relevant tests. 6. Update project status documents. 7. Commit with a
concise, descriptive message. 8. Push the intended branch.

Never commit: - API keys, passwords, tokens or certificates. - SQLite
database files or backups. - User exports or monitoring history. -
`bin/`, `obj/`, `.vs/`, test results or other build output. - Local IDE
state or machine-specific settings.

Maintain an appropriate `.gitignore`.

## 4. Build and test requirements

-   Target .NET 10 LTS unless the user authorizes a change.
-   Use nullable reference types.
-   Enable implicit usings where appropriate.
-   Use async APIs for I/O and network operations.
-   Use cancellation tokens for long-running operations.
-   Avoid `async void` except for framework event handlers.
-   Treat compiler warnings seriously. Do not suppress warnings without
    a documented reason.
-   Keep NuGet dependencies current and justified, but do not perform
    broad dependency upgrades without need.
-   Build the full solution in Release configuration before a release
    push.
-   Run all relevant automated tests.
-   If a test cannot run, report why and what remains unverified.

Typical commands:

``` powershell
dotnet --info
dotnet restore NetTrace.sln
dotnet build NetTrace.sln -c Release
dotnet test NetTrace.sln -c Release
```

Adjust solution paths only after inspecting the repository. Do not
assume project names or paths if they differ.

## 5. Architecture rules

Keep the layered architecture described in `plan.md`.

Expected solution structure:

``` text
NetTrace.sln
src/
  NetTrace.App/
  NetTrace.Core/
  NetTrace.Infrastructure/
  NetTrace.Reporting/
tests/
  NetTrace.Core.Tests/
  NetTrace.Infrastructure.Tests/
  NetTrace.Reporting.Tests/
```

Dependency direction: - Core contains domain types, business rules and
interfaces. - Infrastructure implements persistence, Windows integration
and network probes. - Reporting implements export and integrity
functions. - App hosts WPF, dependency injection, view models and
application lifecycle. - Tests validate behaviour independently of live
internet access.

Do not put monitoring algorithms in WPF code-behind. Keep code-behind
limited to UI/framework integration. Use MVVM for UI state and commands.

Do not let Core depend on WPF, SQLite implementation types, Windows UI
APIs or concrete HTTP clients.

## 6. Monitoring-engine rules

-   Never block the UI thread.
-   Use asynchronous, cancellation-aware operations.
-   Prevent overlapping probe cycles.
-   Apply explicit timeouts to every network probe.
-   Bound concurrency and memory usage.
-   Do not create unbounded tasks, timers or queues.
-   Discover the active interface and default gateway dynamically.
-   Refresh network context after interface/address changes and resume
    from sleep.
-   Use multiple independent external targets and protocols.
-   Record the target, protocol, timeout, timestamp and outcome for each
    probe.
-   Treat unavailable measurements as null or an explicit unavailable
    state, never as zero.
-   Do not infer ISP responsibility from one failed target.
-   Do not treat ICMP failure alone as proof of internet failure.
-   DNS, HTTPS, gateway and external IP connectivity must remain
    distinguishable.
-   Make probe endpoints configurable and document defaults.
-   Do not perform continuous speed tests. The live throughput display
    is allowed because it only reads OS interface byte counters and
    sends no traffic.
-   Public IP lookup, if enabled, must be infrequent, configurable and
    disclosed.

## 7. Incident detection and classification rules

-   A single failed probe must not create a confirmed outage.
-   Use configurable consecutive-failure thresholds and corroborating
    probes.
-   Keep suspected and confirmed incidents distinct.
-   Record raw evidence used to classify each incident.
-   Record incident start, detection, recovery and duration separately.
-   Close incidents only after configured recovery checks.
-   Avoid splitting one incident into many events due to brief recovery
    flickers.
-   Use cautious labels such as `ExternalConnectivityUnavailable` or
    `PossibleUpstreamIssue`.
-   Never claim an ISP fault is proven when the measurements only show
    external connectivity failure.
-   Keep classification logic in Core and cover it with deterministic
    unit tests.

## 8. Time and monitoring-gap rules

-   UTC is authoritative for stored timestamps.
-   Store local timezone context for display and day-wise reporting.
-   Use ISO 8601 for serialized timestamps.
-   Inject a clock/time provider into domain services that depend on
    time.
-   Handle local midnight and timezone transitions correctly.
-   Detect significant system-clock discontinuities where practical.
-   Record monitoring gaps for sleep, shutdown, application exit, crash
    recovery, disabled monitoring and other known interruptions.
-   Unknown gaps must remain unknown. Do not relabel them as outages.
-   Uptime calculations must exclude unmonitored periods.
-   Clearly document the denominator used for uptime and coverage
    percentages.

## 9. SQLite and data-integrity rules

-   Store mutable application data under `%LOCALAPPDATA%\\NetTrace\\`,
    not beside the executable.
-   Use schema migrations.
-   Use transactions for related writes.
-   Add indexes for timestamp, target/probe type and incident date
    queries.
-   Use parameterized queries through the chosen data-access layer.
-   Handle database lock/busy errors with bounded retry.
-   Avoid long-lived write transactions.
-   Batch writes where appropriate.
-   Do not delete historical data during upgrades.
-   Retention defaults to Forever.
-   Retention cleanup must be explicit, tested and logged.
-   Preserve daily aggregates and incident records according to the
    retention design.
-   Never commit a live database, database backup or user monitoring
    history.

## 10. Windows and WPF rules

-   Use WPF on .NET 10 LTS.
-   Use MVVM and dependency injection.
-   Keep long-running monitoring independent of window lifetime.
-   Implement a single-instance strategy.
-   Implement system tray behaviour with explicit Open, Start/Stop
    Monitoring, Export Today, Settings and Exit actions.
-   Implement the optional live throughput display (see `plan.md`
    section 4.11): sample interface counters off the UI thread, update
    the tray icon/tooltip at most once per sample interval, and show
    unavailable rather than zero when counters cannot be read.
-   Keep throughput display-only in v1: do not persist it, export it or
    feed it into incident detection.
-   Closing the window must follow the configured close-to-tray/exit
    behaviour.
-   Provide a clear Exit action.
-   Startup-at-login must be optional and reversible.
-   Prefer per-user startup registration; do not require elevation for
    ordinary startup configuration.
-   Do not install a Windows Service for v1.
-   Handle Windows sleep/resume and network-change events, then
    reconcile actual interface state.
-   Ensure installer/uninstaller does not remove user monitoring data
    without explicit user choice.
-   Keep UI updates on the dispatcher and throttle chart refreshes to
    avoid excessive rendering.

## 11. Privacy and security

-   No cloud dependency or outbound telemetry by default.
-   Do not collect packet payloads or browse history.
-   Never store Wi-Fi passwords or credentials.
-   Do not log secrets.
-   Sanitize exception messages before persisting or exporting.
-   Provide export redaction for SSID, local IP, gateway and public IP.
-   Explain that network metadata may be sensitive.
-   Validate file paths and use safe file creation patterns.
-   Do not load arbitrary executable content from exports.
-   Hash exports with SHA-256 and describe the limitations accurately: a
    hash detects post-export changes but does not independently prove
    measurement authenticity.

## 12. Reporting rules

-   Version the JSON schema.
-   Use stable property names and documented units.
-   Include methodology, probe interval, endpoints, timeouts, thresholds
    and timezone.
-   Include monitoring gaps.
-   Include data-quality notes.
-   Include raw observations for detailed exports.
-   Include summaries and incidents.
-   Use null for unavailable values.
-   Ensure CSV column names and units are documented.
-   Support single-day and date-range exports.
-   For multi-file exports, create a manifest with filenames, sizes and
    SHA-256 hashes.
-   Never export credentials or secrets.
-   Do not claim the export proves ISP fault. It provides timestamped
    evidence of observed network conditions.

## 13. Testing rules

Core monitoring and classification tests must not depend on live public
internet access.

Use: - Fake probes. - Fake network-interface providers. - An injectable
clock. - Temporary SQLite databases. - Deterministic fixtures.

Required coverage includes: - Outage confirmation and recovery. -
Gateway reachable/external unavailable. - Gateway unavailable. - DNS
failure while IP connectivity works. - HTTPS success with ICMP
failure. - Destination-specific failure. - Latency and jitter
calculations. - Packet loss calculations. - Daily boundary and timezone
behaviour. - Monitoring gaps and uptime denominator. - Retention. -
Database migrations and repository queries. - JSON/CSV serialization. -
SHA-256 verification. - Startup registration enable/disable where
testable.

Do not weaken tests merely to make a build pass. Fix the implementation
or explain a legitimate platform limitation.

## 14. UI and UX rules

-   Use a consistent, modern Windows 11-inspired visual language.
-   Prefer clear information hierarchy over decorative elements.
-   Keep status labels readable and explicit.
-   Never use colour as the only indicator.
-   Distinguish Healthy, Degraded, Outage, Monitoring Paused and
    Unmonitored.
-   Provide tooltips or help text for technical metrics.
-   Display units on all measurements.
-   Explain uptime and monitoring coverage.
-   Show the timestamp of the latest successful measurement.
-   Make the current monitoring state obvious.
-   Avoid unnecessary animation and heavy rendering.

## 15. Implementation workflow

For every task:

1.  Read `plan.md`, this file, `currentstate.md` and relevant source
    files.
2.  Inspect Git status and current branch.
3.  Identify the smallest coherent implementation.
4.  Explain the intended change briefly before substantial work.
5.  Implement production code and tests together.
6.  Build the affected project.
7.  Run relevant tests.
8.  Build the full solution in Release when the change is complete.
9.  Update `changelog.md`.
10. Update `currentstate.md` with actual build/test status and next
    steps.
11. Review the diff and staged files.
12. Commit and push according to the Git cadence above.

Do not batch unrelated features into one large change if a smaller
buildable increment can be pushed safely.

## 16. Release management

Use semantic versioning: - MAJOR: incompatible data/schema or
user-facing changes. - MINOR: backward-compatible features. - PATCH:
fixes and small improvements.

For every release: - Update application version consistently. - Review
database migration behaviour. - Build Release configuration. - Run
automated tests. - Verify installer upgrade and uninstall behaviour. -
Verify user data is preserved. - Verify startup registration is cleaned
up appropriately. - Verify no local data or secrets are packaged. -
Update changelog and current state. - Produce release notes. - Publish
only after successful validation.

If distributing through GitHub Releases, provide versioned release
assets and checksums. Do not publish installer artifacts until the
release build and smoke tests pass.

## 17. Required status-document format

### `changelog.md`

Maintain newest entries first. Each entry should include: - Version or
`Unreleased`. - Date. - Added. - Changed. - Fixed. - Tests/validation
where useful.

Only record work that actually happened.

### `currentstate.md`

Keep these headings: 1. Project status. 2. Current version. 3. Current
branch. 4. Implemented features. 5. In-progress work. 6. Known issues
and limitations. 7. Last successful build. 8. Last test result. 9. Last
commit/push. 10. Next recommended tasks.

Do not claim a build, test, commit or push occurred unless it was
actually executed.

## 18. First implementation task checks

Begin with repository inspection and project scaffolding.

1.  Confirm the repository is empty or identify existing work.
    Initialize Git if needed and set `origin` to
    `https://github.com/ranababu1/nettrace.git`.
2.  Create `NetTrace.sln`.
3.  Create the WPF application targeting `net10.0-windows`.
4.  Create Core, Infrastructure and Reporting class libraries targeting
    compatible .NET 10 frameworks.
5.  Create test projects.
6.  Configure project references according to the architecture.
7.  Add `.gitignore`, `README.md`, `plan.md`, `instructions.md`,
    `changelog.md` and `currentstate.md`.
8.  Add dependency injection and structured logging.
9.  Add a basic WPF shell that starts and exits cleanly.
10. Build the full solution in Release and run tests.
11. Commit and push the first buildable scaffold according to the Git
    rules.

Do not implement the entire monitoring engine in the initial scaffolding
task.

------------------------------------------------------------------------

## 19. Completion standard

A task is complete only when: - The requested behaviour is implemented,
not merely stubbed. - Relevant tests exist and pass. - The solution
builds. - Error and cancellation paths are handled. - Documentation is
updated. - No unrelated user changes are lost. - Git status and diff
have been reviewed. - The required commit/push cadence has been followed
or the blocker has been reported.
