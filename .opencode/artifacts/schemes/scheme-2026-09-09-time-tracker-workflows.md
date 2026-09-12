# Time Tracker Workflows

**Date:** 2026-09-09
**Status:** complete

---

## Goal

Make task and board capture available where work is reviewed, add a first-class Time Tracker workspace for standalone activities, and let the Today dashboard deliberately start or switch tracked activities.

## Attack Plan

- Add board, project, and task capture controls to Tasks while preserving the existing revision-checked backend workflows.
- Add a Time Tracker navigation section with activity/group creation, activity editing/lifecycle controls, one-click activity timers, active-session controls, and manual entry.
- Add activity selection and quick switching to Today, sharing the same activity/session state and commands as Time Tracker.
- Extend screenshot coverage and update user-facing implementation notes.

## Testing and Verification

- Add application coverage for foreground activity switching and standalone activity sessions.
- Run the pinned solution build and complete test suite.
- Capture seeded Today, Tasks, and Time Tracker screens and inspect the rendered output.
- Run `git diff --check` and an independent end-to-end validation review.

## Success Conditions

- Users can create boards/projects/tasks without leaving Tasks.
- Users can create, edit, start, pause/resume, stop, archive, restore, and delete standalone activities from Time Tracker.
- Today can select and start another activity, preserving prior foreground work as a resumable paused session under the existing concurrency policy.
- Embedded and daemon behavior remain equivalent because all UI actions use `IBackendClient`.

## Risks / Open Questions

- A task requires a project, so Tasks must expose project creation in addition to the requested board and task creation.
- Starting a new foreground target pauses (rather than stops) the previous foreground session unless concurrent foreground timers are enabled; the UI must explain this clearly.
