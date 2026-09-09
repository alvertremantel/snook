# Architecture

## Platform baseline (2026-09-08)

- .NET 10 LTS / C# 14. .NET 10 support runs through November 2028.
- Avalonia 12.1.x; `12.1.2` was current during research. All Avalonia packages
  are centrally pinned to one version.
- `Microsoft.Data.Sqlite` 10.0.x with its compatible SQLitePCLRaw dependency.
- `Microsoft.Extensions.Hosting` for desktop composition and daemon worker host.
- gRPC/HTTP/2 contracts where a process/network boundary is required.
- xUnit 3 or NUnit 4 aligned with Avalonia 12 headless-test guidance.

The checked-in `global.json` requests the .NET 10 SDK family. Exact NuGet
versions are pinned in `Directory.Packages.props` when projects are scaffolded,
then updated through reviewed dependency PRs.

Avalonia Android is shipped but still described as experimental upstream. It is
an explicit release risk, not assumed parity. The initial proposed Android
minimum is API 24, with current target SDK and required ABIs fixed by the release
matrix.

## Dependency direction

```text
                           ┌──────────────────┐
                           │ Snook.Domain   │
                           └────────▲─────────┘
                                    │
                         ┌──────────┴──────────┐
                         │ Snook.Application │
                         └───▲───────────▲─────┘
                             │           │
             ┌───────────────┘           └───────────────┐
             │                                           │
┌────────────┴────────────┐                 ┌────────────┴─────────┐
│ Persistence.Sqlite      │                 │ Infrastructure.Sync  │
└────────────▲────────────┘                 └────────────▲─────────┘
             │                                           │
             └──────────────────┬────────────────────────┘
                                │
                       ┌────────┴─────────┐
                       │ Backend.Hosting │
                       └──▲───────────▲───┘
                          │           │
                 ┌────────┘           └─────────┐
          Desktop/Android/CLI                 Daemon/API
```

Contracts define transport DTOs and error semantics but do not leak SQLite or
Avalonia types. Domain has no I/O dependencies. Application owns use cases,
transactions-as-ports, authorization policy, clocks, and result types.

## Proposed solution projects

```text
src/
  Snook.Domain/
  Snook.Application/
  Snook.Contracts/
  Snook.Persistence.Sqlite/
  Snook.Infrastructure.Sync/
  Snook.Backend.Hosting/
  Snook.Client/
  Snook.UI/
  Snook.Desktop/
  Snook.Android/
  Snook.Daemon/
  Snook.Cli/
tests/
  Snook.Domain.Tests/
  Snook.Application.Tests/
  Snook.Persistence.Tests/
  Snook.Contracts.Tests/
  Snook.Client.ContractTests/
  Snook.Sync.Tests/
  Snook.UI.HeadlessTests/
  Snook.EndToEnd.Tests/
benchmarks/
  Snook.Benchmarks/
```

`Snook.UI` contains shared Avalonia views/view-models/resources.
`Snook.Desktop` and `Snook.Android` are platform composition roots. Platform
services (notifications, secure storage, file picking, lifecycle) are injected
through narrow interfaces.

## Backend host modes

### Embedded

- Default desktop/local profile and default Android profile.
- UI process creates backend host in-process and owns store lease/migrations.
- `IBackendClient` is an in-process adapter over application services.
- No loopback socket or serialization is required.
- Closing the process ends hosting; durable open intervals can continue by UTC
  semantics and are reconciled on next start.

### Local daemon client

- Windows/Linux daemon exclusively owns database, background sync, backup, and
  mutation stream.
- Avalonia and CLI use local authenticated IPC and never load persistence code
  as a fallback.
- UI may close while timing continues in the daemon.
- Daemon runs as a foreground diagnostic process, Windows Service, or systemd
  service. Per-user daemon is preferred for a personal workspace; system-wide
  service requires a dedicated identity and explicit data ownership.

### Remote daemon client

- Useful for Android or a thin desktop profile connecting to a self-hosted
  Snook daemon.
- Uses TLS TCP, explicit pairing, and device authorization.
- Offline behavior is explicit: v1 thin-client mode may be unavailable while
  disconnected rather than pretending to be local-first. Offline replicated
  client mode belongs to sync, not RPC caching.

### Android host constraints

- Do not model Android as a Windows/Linux service host.
- Embedded DB lives in app-private storage.
- Background synchronization uses lifecycle-aware scheduled work; an active
  foreground service requires a visible notification and product justification.
- A desktop-style local gRPC daemon/UDS is not the baseline. Remote TLS gRPC is
  supported; app-internal direct calls are preferred.
- Process suspension does not require a ticking worker. UI computes elapsed on
  resume from durable start and current time.

## Internal runtime

`Backend.Hosting` composes:

- application command/query dispatcher;
- SQLite store and writer queue;
- in-memory change publisher backed by mutation-log cursor;
- store lease and migration coordinator;
- clock/time-zone provider;
- backup/export coordinator;
- optional sync engine;
- health/readiness and structured logging.

Command flow:

```text
UI/CLI/RPC request
  -> client validation/cancellation
  -> application command + authorization + idempotency
  -> bounded writer queue
  -> one SQLite transaction (state + mutation + receipt)
  -> commit
  -> publish cursor/change summary
  -> response
```

Query flow uses read-only snapshots, projection DTOs, cursor pagination, and
cancellation. It never updates “last viewed” or cache state in the domain DB as
a side effect.

## Client/UI architecture

- MVVM without business logic in code-behind.
- View-models depend on `IBackendClient`, navigation, platform capability, and
  UI scheduler abstractions.
- One application-state coordinator owns active session snapshots. A local
  periodic UI clock invalidates only displayed elapsed properties; it does not
  call the backend.
- Mutation notifications invalidate/update targeted projections by cursor.
- Losing a notification stream triggers one reconciliation snapshot, not a
  permanent poll loop.
- Desktop and phone use shared semantics and view-models with adaptive views,
  not a forcibly identical layout.
- Native title bars/window behavior are the initial default; custom chrome is
  postponed until platform/accessibility behavior is proven.

## Optional sync architecture

Sync is downstream of locally committed mutations:

1. A local command commits aggregate changes and mutation log atomically.
2. Sync reads immutable operations after a peer cursor.
3. Authenticated peers exchange versioned, bounded batches over encrypted
   transport.
4. Receiver verifies identity, replay/idempotency, schema capability, and
   dependency ordering.
5. Remote operation is applied through a replication service with the same
   invariants, producing explicit merge/winner/conflict outcome.
6. Ack advances peer cursor only after commit.

Stable IDs, operation IDs, tombstones, and deferred dependency handling from the
legacy design are retained conceptually. Unauthenticated raw TCP, self-asserted
device identity, full-row trigger JSON, and silent device-ID tie-break loss are
not.

Conflict strategy is hybrid:

- independent set membership and append-only intervals can merge;
- scalar concurrent edits use deterministic policy only where loss is benign;
- destructive or semantically competing edits preserve both payloads as an
  unresolved conflict;
- conflict list/resolution is part of the backend contract and UI capability.

Detailed sync conflict algorithms receive a dedicated follow-up spec before
FR-SYNC requirements are scheduled.

## Packaging and deployment

- Desktop: self-contained, signed artifacts for explicit Windows/Linux RIDs;
  package formats use the selected platform identifiers and updates are user-initiated in-app.
- Android: signed AAB/APK release output, ABI/native SQLite validation, backup
  rules, and store policy compliance.
- Daemon: self-contained foreground binary plus service definitions for Windows
  and systemd; localhost/IPC only until remote access is configured.
- CLI: may ship with desktop/daemon bundles but remains a separate executable.

## Observability

- Structured logs with event IDs, operation/correlation IDs, durations, schema
  version, host mode, and redacted exception category.
- Metrics are local and opt-in to export; no vendor telemetry endpoint exists.
- Health distinguishes process live, store ready, migrations complete, writer
  queue healthy, disk writable, backup status, and optional sync state.
- Diagnostic bundle requires preview/consent and excludes DB/content/secrets by
  default.

## Current-source references

- [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)
- [Avalonia releases](https://github.com/AvaloniaUI/Avalonia/releases)
- [Avalonia Android guide](https://docs.avaloniaui.net/docs/platform-specific-guides/android/)
- [Microsoft.Data.Sqlite](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/)
- [ASP.NET Core interprocess gRPC](https://learn.microsoft.com/en-us/aspnet/core/grpc/interprocess?view=aspnetcore-10.0)
- [.NET Worker Services](https://learn.microsoft.com/en-us/dotnet/core/extensions/workers)
- [Avalonia testing](https://docs.avaloniaui.net/docs/testing)
