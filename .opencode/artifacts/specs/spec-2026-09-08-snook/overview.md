# Overview

## Problem

Legacy Grouper proved the product idea: task boards, a fast activity timer,
calendar planning, history, and summaries belong in one private productivity
space. Its implementation also exposed the cost of treating those capabilities
as adjacent database features. Tasks and time were joined late, mutable names
acted as identities, UI timers polled SQLite every second, derived views used
inconsistent time semantics, migrations could fail before they ran, and network
sync expanded the threat surface without authentication.

Snook is not a line-by-line port. It is a coherent local application
platform whose desktop/mobile UI, CLI, daemon, and future sync all
invoke the same domain operations.

## Product statement

**Snook is a private, local-first workspace where planning work and recording
the time spent doing it are two views of the same activity.**

A user can plan a task, begin focus directly from that task, pause or resume
without database churn, see the resulting work on the task/project/calendar,
and understand history without exporting data to a service. General activities
remain available for time that is not tied to a task.

## Goals

1. Deliver dependable task and time management on Windows, Linux, and Android.
2. Make time attribution a first-class part of tasks and projects rather than a
   note added when stopping a timer.
3. Persist only meaningful state transitions; render elapsed time in memory.
4. Keep SQLite understandable, constrained, migratable, repairable, and fast.
5. Support embedded operation with no daemon and client-only operation through
   an optional daemon without changing domain semantics.
6. Be private by default: no account, cloud, telemetry, or open listener.
7. Make secure, encrypted, explicitly paired peer sync possible without making
   it foundational to local correctness.
8. Establish production gates for accessibility, recovery, security,
   packaging, and real-device behavior.

## Non-goals for the first production release

- Multi-user collaboration, shared workspaces, roles, or enterprise tenancy.
- A vendor-operated cloud or web account system.
- Browser parity with the native client.
- Arbitrary plugins or third-party code execution.
- Full CalDAV, Exchange, Jira, or similar external-service integration.
- Pixel-for-pixel reproduction of the PySide6 interface.
- Opening or importing the legacy SQLite file as the live new database.
- A permanently running Android daemon; Android lifecycle rules take priority.

## Stakeholders and primary modes

| Stakeholder/mode | Need |
|---|---|
| Individual desktop user | Fast offline app with owned local data |
| Android user | Responsive local client that survives mobile lifecycle constraints |
| Power user | CLI, export, diagnostics, and controllable data location |
| Self-hoster | Headless Windows/Linux daemon with secure clients and backups |
| Maintainer | Testable boundaries, observable failures, safe schema evolution |

## Product principles

### One domain, many views

Dashboard, board, list, calendar, history, summary, CLI, and daemon API are
projections and commands over the same task/time model. No view owns alternate
business rules.

### Meaningful writes only

The passing of a second is not a data mutation. An open interval plus the
current clock is enough to display elapsed time. Start/pause/resume/stop and
explicit edits are transactions; timer animation is local presentation.

### Stable identity, mutable labels

Names and titles can change without rewriting history. Every durable entity and
mutation has a stable UUID; database row integers are implementation details if
used at all.

### Explicit host ownership

Exactly one backend host owns a data store at a time. In embedded mode that is
the UI process. In daemon mode it is the daemon, and clients never open its DB.

### Local-first, not local-only

Embedded and local-daemon profiles provide every primary workflow without an
external network. A user who explicitly selects a remote thin-client profile
accepts that profile's documented offline limitation. Optional replicated sync
never becomes a prerequisite for local profiles to open, edit, or track time.

### Honest privacy

The product describes what leaves the device, when, and why. Discovery is not
trust, transport encryption is not peer authorization, and encryption at rest
is not claimed unless the shipped database actually provides it.

## Definition of success

- A timer can run for 24 hours with no periodic database writes and, in a
  single-client process, no periodic database reads.
- Pause/resume boundaries, restart recovery, cross-midnight reports, DST, and
  manual corrections have deterministic tests.
- Starting time from a task requires at most one action after configuration;
  the task and project immediately expose active and historical time.
- Embedded and daemon-backed clients pass the same contract test suite.
- The live DB cannot be opened concurrently by an embedded app and its daemon.
- Desktop packages install/update on supported Windows and Linux targets;
  Android release builds pass emulator and real-device gates.
- Enabling sync requires explicit pairing and authenticated encryption.
- No telemetry or external network request occurs in the default local profile.
