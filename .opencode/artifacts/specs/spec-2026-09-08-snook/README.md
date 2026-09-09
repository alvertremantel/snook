# Snook

**Date:** 2026-09-08  
**Status:** draft for owner review  
**Scope:** production-grade C#/.NET + Avalonia replacement for legacy Grouper

## Purpose

This package defines a local-first task-and-time product for Windows, Linux,
and Android. It preserves the strongest legacy workflows while replacing the
database, timer, process, sync, security, and delivery foundations.

The central architectural rule is simple: **time advances in memory; durable
writes occur only at user-meaningful boundaries** such as start, pause, resume,
stop, edit, and recovery. Every UI and host mode uses the same application
contract, whether the backend runs inside the app or in a headless daemon.

## Recommended reading order

1. [overview.md](./overview.md) — vision, scope, principles, and success measures
2. [legacy-audit.md](./legacy-audit.md) — observed behavior and defects in the ZIP
3. [product-model.md](./product-model.md) — terminology, entities, and workflows
4. [requirements.md](./requirements.md) — numbered functional and quality requirements
5. [data-model.md](./data-model.md) — persistence, timekeeping, integrity, and queries
6. [architecture.md](./architecture.md) — solution boundaries and host modes
7. [interfaces.md](./interfaces.md) — backend contract, local IPC, and remote API
8. [security-privacy.md](./security-privacy.md) — threat model and privacy posture
9. [interfaces.md](./interfaces.md) — versioned application contract and host APIs
10. [delivery-plan.md](./delivery-plan.md) — milestones and release gates
11. [open-questions.md](./open-questions.md) — owner decisions that remain open

## Decisions recorded

- [ADR-0001: Boundary-written timekeeping](./decisions/ADR-0001-boundary-written-timekeeping.md)
- [ADR-0002: One backend contract, embedded and daemon hosts](./decisions/ADR-0002-backend-parity.md)
- [ADR-0003: SQLite with one logical writer](./decisions/ADR-0003-sqlite-single-writer.md)

## Status by concern

| Concern | Status |
|---|---|
| Legacy behavior inventory | Complete enough to specify the replacement |
| Product and domain baseline | Proposed |
| Persistence/timekeeping baseline | Proposed; primary implementation target |
| Desktop/daemon boundaries | Proposed |
| Android feasibility | Feasible with an explicit experimental-platform gate |
| Peer sync | Architecture-ready, post-local-core delivery |
| Legacy database migration | Not supported; legacy Grouper remains reference-only |
| Product license | Decided: proprietary, closed source |

## Current open questions

The product name, proprietary closed-source licensing, v1 sync scope, timer
concurrency policy, host defaults, platform targets, visual direction, and
dependency choices are decided. Remaining implementation follow-ups are tracked
in [open-questions.md](./open-questions.md).

The defaults proposed in [open-questions.md](./open-questions.md) let engineering
begin without silently deciding product policy.

## Source basis

- Legacy archive: `refs/grouper-main.zip`
- SHA-256: `2b26bb9d5fc8262614d50a00d82a7b66af2c050355da3994c2c8f2b24fac46d0`
- Embedded archive revision marker: `1e92e408bcc9df5db055c74edb41f512e17302ea`
- Archive scale: 253 files after extraction; approximately 3.6 MiB
- Independent reviews: product/UI, persistence/timekeeping, daemon/sync/security,
  migration/test quality, plus current platform research

## Change log

- 2026-09-09: renamed the product and solution to Snook; recorded proprietary,
  closed-source licensing.
- 2026-09-08: initial specification package and repository foundation
