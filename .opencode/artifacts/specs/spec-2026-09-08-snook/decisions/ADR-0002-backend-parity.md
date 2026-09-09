# ADR-0002: One Backend Contract, Embedded and Daemon Hosts

**Status:** accepted for baseline  
**Date:** 2026-09-08

## Context

The product must work as a normal offline app without mandatory service setup,
while also supporting a client that functions only through a headless daemon.
Maintaining separate in-process and server business logic would recreate legacy
drift and make invariants dependent on the entry point.

Android cannot be treated like a desktop service OS, and process-local calls do
not need transport overhead.

## Decision

Define one versioned application/backend contract and client abstraction.

- Embedded hosts execute it in-process.
- Windows/Linux daemon hosts expose it over protected local IPC and optionally
  authenticated TLS remote transport.
- UI/view-models and CLI depend only on the client abstraction.
- Daemon-only profiles fail closed when disconnected and never open the DB.
- Both adapters run the same semantic contract tests.
- Android defaults to in-process and may use remote TLS; a local always-running
  daemon is not assumed.

## Consequences

Positive:

- Domain behavior is consistent across UI, CLI, and daemon.
- Optional daemon remains a deployment decision, not a second product.
- Client-only operation is enforceable and testable.
- Secure remote access and future alternate clients have a narrow boundary.

Costs:

- Contracts require deliberate versioning, DTO mapping, pagination, typed
  errors, and notification cursors.
- In-process adapter must resist exposing convenient implementation-only APIs.
- Transport parity tests increase test surface.
- Thin remote mode needs explicit disconnected UX; it is not offline replication.

## Rejected alternatives

- **Mandatory daemon everywhere:** harms zero-setup/offline desktop use and is a
  poor Android model.
- **UI opens DB even in daemon mode:** permits conflicting ownership and bypasses
  service policy.
- **Separate REST/server business layer:** duplicates rules and makes parity
  aspirational.
- **Always use loopback RPC, even embedded:** adds startup/auth/serialization
  failure modes without product value.
