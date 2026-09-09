# Open Questions and Proposed Defaults

These do not invalidate the architecture. Proposed defaults are safe starting
points that should be confirmed before the named delivery gate.

| ID | Question | Proposed default | Decide by |
|---|---|---|---|
| OQ-001 | Product/source license? | **Decided:** Snook is proprietary and closed source; all rights reserved by the licensor. Legacy archive remains AGPL-3.0 and is reference-only. | Decided |
| OQ-002 | Concurrent foreground sessions? | **Decided:** make this a setting. When disabled, starting a foreground timer pauses the currently running foreground timer; when enabled, multiple foreground timers may run. | Decided |
| OQ-003 | Sync in first public release? | **Decided:** no peer sync in v1. Ship the secure local/daemon product first. | Decided |
| OQ-004 | Android host modes? | **Decided:** the default Android profile is a client-host with its backend embedded in the app. A remote-daemon client profile may be offered separately; Android will not run a permanent desktop-style daemon. | Decided |
| OQ-005 | Desktop default host mode? | **Decided:** the default desktop profile is a client-host with its backend embedded in the app. A per-user daemon profile remains available for users who want persistent hosting/API access. | Decided |
| OQ-006 | Remote daemon client offline semantics? | **Decided:** thin clients fail explicitly while offline; replicated offline mode is deferred with sync. | Decided |
| OQ-007 | Activity required when timing a task? | **Decided:** no. A session requires a task or activity; a task may infer an optional activity default. | Decided |
| OQ-009 | At-rest encryption in v1? | **Decided:** use OS-private storage and optional encrypted exports first; run the SQLCipher/provider spike before promising database encryption. | Decided |
| OQ-010 | Supported Windows/Linux versions and packages? | **Decided:** target Windows 11 and Fedora Linux 44; define the corresponding package formats during the packaging spike. | Decided |
| OQ-011 | Android minimum API? | **Decided:** API 24 minimum, with the current target SDK at release, subject to Avalonia/.NET constraints. | Decided |
| OQ-012 | Task status depth? | **Decided:** open/completed in v1 plus archive/delete; defer custom workflows and subtasks. | Decided |
| OQ-013 | Boards own projects or are views? | **Decided:** preserve one-board-per-project for simplicity; multi-board views are future work. | Decided |
| OQ-014 | Calendar recurrence library/representation? | **Decided:** use standards-compatible RRULE with zoned civil-time tests and the MIT-licensed `Ical.Net` library. | Decided |
| OQ-015 | Auto-update? | **Decided:** automatic updates are disfavored. Provide an easy, explicit, user-initiated in-app check and update flow. | Decided |
| OQ-016 | Daemon service identity? | **Decided:** use a per-user daemon. A system service is not the default and would require explicit workspace ACL mapping. | Decided |
| OQ-017 | Data purge/tombstone retention? | **Decided:** retain deleted data indefinitely by default. Add explicit user-invoked permanent-delete features; sync-aware tombstone cleanup can be added with the later sync design. | Decided |
| OQ-018 | Branding/name? | **Decided:** display name is “Snook”; use Android application ID `com.snook.app`, Windows package identity `com.snook.desktop`, and Linux application ID `com.snook.Snook`. | Decided |
| OQ-019 | macOS? | **Decided:** no macOS support. | Decided |
| OQ-020 | Legacy visual identity? | **Decided:** retain very little of it. Snook should be simpler and cleaner rather than reproducing the legacy density, dark theme, or chrome. | Decided |

## Remaining inputs and implementation follow-ups

There are no remaining owner decisions. The selected recurrence library,
platform identifiers, and product scope are implementation inputs.

## Deferred specifications

Create focused follow-up specs before implementation for:

- peer sync protocol/conflict/compaction and device key lifecycle;
- exact calendar recurrence semantics/library;
- packaging, signing, update channels, supported OS matrix;
- at-rest/encrypted-backup capability if selected;
- remote thin-client authorization if it ships before sync.
