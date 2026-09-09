# Delivery Plan

## Strategy

Build thin vertical slices through the backend contract and new SQLite model.
Do not begin by reproducing every legacy screen. Timer correctness, task/time
integration, persistence evidence, and host parity are the risk-first path.

Sync is deliberately after a complete secure local product. The data model and
mutation log remain sync-ready, but no unauthenticated “temporary” network mode
is permitted.

## Milestone 0 — Decisions and repository foundation

Deliver:

- Resolve blocking items in `open-questions.md`, especially concurrency default,
  Android mode, and release sync scope.
- Scaffold solution projects/dependency tests from `architecture.md`.
- Pin packages centrally; add formatter/analyzers, unit test framework, CI, SBOM
  and vulnerability/license reporting.
- Establish Windows/Linux build matrix and Android workload lane.
- Convert numbered requirements into tracked acceptance tests/issues.

Exit gate:

- Clean restore/build/test on Linux and Windows.
- Dependency-direction test prevents UI/daemon/persistence leakage.
- Third-party notice strategy approved; proprietary product licensing recorded.

## Milestone 1 — Persistence kernel

Deliver:

- Reviewed v1 DDL, migrations, store lease, SQLite operating profile, writer
  queue, transaction abstraction, operation receipts, and mutation log.
- Task/activity/session/interval minimal aggregates and repositories.
- Integrity scanner, online backup/restore primitive, fault-injection harness.
- Clock/time-zone test harness using `TimeProvider`.

Exit gate:

- Start/pause/resume/stop repository tests pass under duplicate/concurrent/fault
  scenarios.
- Cross-midnight/DST/clock rollback fixtures pass.
- Store ownership conflict is verified cross-process.

## Milestone 2 — Integrated timer vertical slice

Deliver:

- Application commands/queries and `IBackendClient` in-process adapter.
- Minimal Avalonia shell on Windows/Linux: activity/task quick start, active
  session cards, pause/resume/stop, recovery.
- In-memory elapsed display and targeted change notifications.
- Task detail shows active and accumulated time.
- Headless Avalonia and backend contract tests.

Exit gate:

- 24-hour virtual/accelerated timer test records no display-tick reads/writes.
- Lifecycle acceptance cases FR-TIME/NFR-COR pass.
- Kill/restart recovery and stale command UX pass.

## Milestone 3 — Planning core

Deliver:

- Boards, projects, tasks, dependencies, tags, groups, links, stars, archive/
  restore, board/list/search, quick capture.
- Completion and dependency invariants through all entry points.
- Start-from-task with activity inheritance.
- Today projection and task/project time summaries.
- First useful CLI command set with JSON output.

Exit gate:

- Core task workflows are keyboard accessible and contract-tested in embedded
  mode.
- No N+1 behavior on benchmark fixtures.
- Generic update paths cannot bypass completion/session invariants.

## Milestone 4 — Calendar, history, and analysis

Deliver:

- Deadline versus schedule-block model and agenda/week/month views.
- Calendar events, bounded recurrence, exceptions, drag/drop/resizing.
- Searchable paginated history and interval correction workflow.
- Summary dimensions, civil-day overlap, concurrent duration/coverage labels.
- Versioned JSON export and CSV reports.

Exit gate:

- Recurrence/property/range-limit tests pass.
- Cross-zone/DST reports reconcile with interval-level oracle.
- Accessibility and responsive desktop/tablet gates pass.

## Milestone 5 — Daemon and transport parity

Deliver:

- Windows/Linux daemon foreground host, readiness, graceful shutdown, service
  integration, backup jobs, and logs.
- gRPC contracts, named-pipe/UDS clients, protected endpoint descriptor.
- Reconnect/cursor stream behavior and explicit daemon-only client UX.
- CLI supports embedded profile or selected daemon using same contract.

Exit gate:

- In-process and gRPC contract suites are semantically identical.
- Unauthorized local process/user tests fail closed.
- UI never opens DB in daemon-client profile, including outage/restart paths.
- Service install/update/uninstall and timer-continues-after-UI-close tests pass.

## Milestone 6 — Desktop production release

Deliver:

- Windows/Linux package/update strategy, signing, notices, backup/restore UI,
  diagnostics, onboarding, docs.

Exit gate:

- Clean install, upgrade, service, DB relocation, backup/restore, and uninstall
  matrix passes on supported Windows/Linux versions.
- Security/privacy/accessibility/recovery review has no release blockers.

## Milestone 7 — Android production gate

Deliver:

- Android composition root and adaptive phone/tablet UI.
- Embedded app-private SQLite mode and/or remote daemon profile per decision.
- Lifecycle reconciliation, notifications/foreground-service behavior if used,
  Keystore adapter, export via platform pickers.
- Signed AAB/APK and store-ready metadata.

Exit gate:

- Supported API/ABI emulator matrix and representative real devices pass.
- Suspend, process kill, reboot, clock/time-zone change, backup/restore, low
  storage, and network-transition cases pass.
- Native SQLite assets and 16 KiB page-size ecosystem requirements are verified.
- Avalonia Android issues have documented workarounds or block release.

## Milestone 8 — Authenticated peer sync (optional release train)

Prerequisite: dedicated sync protocol/conflict specification and threat review.

Deliver:

- Pairing, key storage/rotation/revocation, encrypted transport.
- Bounded snapshot and mutation replication, tombstones, compaction, conflicts.
- Device/conflict UI, discovery privacy controls, two/three-peer convergence.
- Remote daemon mode security hardening if released with sync.

Exit gate:

- Hostile protocol/fuzz/replay/auth tests pass.
- Property-based convergence under duplicate, reorder, disconnect, concurrent
  edit, delete/update, and partial snapshot cases.
- External security review findings resolved or explicitly accepted.

## Verification layers

| Layer | Focus |
|---|---|
| Domain unit | State transitions, dependencies, time/calendar value objects |
| Application unit | Commands, idempotency, policy, typed errors |
| SQLite integration | DDL constraints, migrations, transactions, query plans |
| Property/fuzz | Interval oracle, recurrence, parser bounds |
| Contract | In-process versus daemon parity |
| Avalonia headless | View-model/control behavior, navigation, adaptive layout |
| Platform E2E | Windows/Linux packages/services; Android lifecycle/devices |
| Fault injection | Kill, disk full, busy DB interruption |
| Performance | Large datasets, query latency, writer queue, memory/startup |
| Security | IPC identity, pairing, TLS, revocation, logging, archive abuse |

## CI/release matrix baseline

- Pull request: format/analyzers, dependency boundaries, domain/application/
  SQLite/contract/headless UI tests on Linux; build Windows targets.
- Main/nightly: Windows and Linux E2E, fault tests, large
  fixture benchmarks with trend reporting.
- Android lane: workload-pinned build and emulator tests; scheduled real-device
  lab run before release candidates.
- Release: reproducible clean checkout, locked dependencies, SBOM/notices,
  vulnerability review, signing, artifact smoke tests, backup compatibility,
  and installation matrix.

## Rollout

1. Developer preview with disposable profiles only.
2. Alpha with export/backup and restore validation.
3. Migration beta after authentic corpus passes; require retained source backup.
4. Desktop stable after production gates.
5. Android stable only after independent platform gate, not merely shared tests.
6. Sync preview/stable on its own security-gated train.

No release automatically deletes a legacy DB or removes the previous executable.

## Success conditions for the initiative

- Requirements trace to tests and release evidence.
- Task/time workflows feel like one product rather than linked subsystems.
- SQLite is quiet while clocks tick, constrained when commands mutate, and
  recoverable when hosts fail.
- Embedded and daemon modes are interchangeable at the client contract, while
  ownership remains unambiguous.
- Users can understand, export, back up, move, and delete their data.
- Network capabilities are private and secure by construction, not by warning.
