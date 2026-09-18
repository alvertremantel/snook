# Workspace redesign

## Current direction: September 2026 modernization

The [intensive review and implementation plan](ui-modernization.md) supersedes the
earlier disclosure-based maintenance layouts described below. The Dashboard split,
calendar time grid, sidebar, and state colors remain the foundation.

Page-wide expanders no longer serve as workspace navigation. Settings has four
explicit categories and readable record rows, with Edit plus a contextual actions
menu. Tracker puts activity creation and manual time above its two scrolling panes;
maintenance lives in the Settings activity library. Tasks uses compact rows,
functional completion controls, a filter popup, and inline card actions. History
uses aligned ledger columns and a correction drawer. Summary highlights the active
grouping, compares attributed durations with bars, and labels clock coverage
separately. Calendar creation uses drawers and detailed editing belongs to Agenda.

Settings hides deleted boards and activities by default, with separate Show deleted
checkboxes. Revealed deleted rows have a tinted background, accent border,
struck-through name, and explicit Deleted label; restore remains in the actions
menu. Tracker description tooltips are absent for blank or whitespace-only text.

Task capture accepts Enter as well as its button. Tasks has a horizontal board
button row in both views, including All boards, with its own scroll arrows.
The selected board and project lanes expose rename, delete, and saved ordering
through contextual menus. Lanes follow saved project order within saved board order.
New boards are immediately selected
and displayed even before they contain projects, with a prompt to add the first
project. Project lanes and task rows follow that selection; dashboard next tasks
remain workspace-wide.

The task sidebar places Tags and always-visible amber Archive/red Delete actions
above Save task, with Move below Save. Save applies only editable task fields;
Move commits immediately, updates the displayed board/project path, and survives
Cancel. Project picker headings separate boards; prerequisite headings separate
board/project groups. Headings cannot be selected, and each choice retains its
context when the picker closes. Board deletion hides its project lanes and restore
reveals them without deleting their data.

The shared utility drawer is 480 pixels wide, with a scrolling form between a
fixed title/Cancel area and a fixed Save/status area. New controls use compiled
bindings and persistent field labels. Record drafts and picker choices are isolated
from backend refreshes; Cancel/Escape discard them; stale saves preserve them with
actionable feedback. Focus cycles inside the editor and returns to the workspace.
The task editor retains its bounded secondary disclosures for relationships and
movement; those are local to the drawer, rather than page navigation.

Notes-only History corrections preserve precise stored timestamps even though the
form displays minutes. This matters for sessions shorter than a minute. Timer
preferences also preserve unsaved changes across background refreshes.

The sections below record earlier design passes and their verification history.

The target is a desktop workspace worth leaving open: a legible place to choose work,
keep several activities in view, and move between tasks, time, and the calendar.
Summary charts are secondary to those daily interactions.

## Direction

Dashboard capture sits on the left, with a soft green card separated from its white
schedule preview by a 14-pixel gap; tracking and next tasks sit on the right.
Navigation glyphs use 22-pixel text in aligned slots beside the existing labels.
Today and Tasks capture project pickers share the task editor's non-selectable
board headings and retain board context beneath the selected project.
Board/project creation
is a right-aligned toolbar action above the lanes (also available in list view).
Its light-dismiss popup keeps creation in place without expanding the page.

- Keep navigation compact and stable. Use space for work, not decorative metrics,
  fictional profile information, or frequently repeated explanatory copy.
- Tracker: adapt Grouper's grouped activity launchers opposite persistent session
  cards. Running, paused, and background work must be distinguishable by words as
  well as color. Use large elapsed clocks and direct pause/resume/stop controls.
  Activity creation, maintenance, and manual entries are secondary disclosures.
- Tasks: make project columns feel like containers and individual tasks like
  movable cards. Provide hover/press feedback, visible drop targets, and an
  equivalent explicit move action. Preserve revision-checked backend mutations.
- Today: show current work, next actions, and the day's schedule together. Avoid
  decorative statistics displacing those workflows.
- Calendar: keep creation secondary to the date surface and connect planned tasks
  to execution. History, summary, and settings should share the same visual grammar.
- Motion should explain manipulation and state changes; no perpetual decorative
  animation on a screen intended to stay open.

## Verification

Use the headless screenshot harness for seeded, empty, active, and paused states,
including the minimum window size. Inspect the images, not only successful rendering.
Verify timer and board actions against disposable SQLite state, then run the full
solution build, tests, and diff checks. Static screenshots alone cannot verify drag
behavior or keyboard access.

Implemented in the first pass: grouped activity launchers; independent activity and
session scrolling; state-colored elapsed clocks; a persistent sidebar focus clock;
disclosed tracker maintenance; horizontal project lanes, empty drop targets, card
hover depth, a floating drag preview, and Escape cancellation. Explicit project moves
remain available in card disclosures. Today exposes upcoming plans beside current
sessions, and status messages now remain visible outside the page scroll.

The harness now exercises real pointer-driven timer controls and card movement,
checking committed SQLite state and refreshed controls. It also verifies Escape
cancellation and captures the drag preview. Empty and minimum-size profiles are
separate from the ordinary seeded capture.

The requested redesign is implemented and the cross-screen audit is complete.
This is development verification, not a completed human accessibility or
multi-platform release audit.

## Calendar and Today pass

The calendar now navigates day/week/month/agenda ranges and returns to today.
Day and week use a full 24-hour local-time grid, with duration-sized controls,
separate all-day rows, split overnight entries, and side-by-side overlap groups.
The schedule opens near planned work, retains its time scroll on refresh, and
marks the current time. Keyboard or pointer selection opens an inspector; planned
work can be started there. Month entries are bounded and selectable. Planning
now explicitly chooses a task, calendar, and time interval instead of choosing
an arbitrary next task and time. Event and plan forms wrap at narrow widths.

Today shows one focus session above four next actions, beside quick capture and
a three-item chronological preview. The full upcoming list, recent work, and
manual entry remain below. Background and other paused/running sessions remain
available through the tracker. Picker options now preserve unchanged instances
and selections when timer notifications refresh the workspace.

The harness verifies calendar date navigation in day/week/month modes, keyboard
inspection, overlap placement bounds, starting a planned task, rejecting an invalid
interval, and persisting the explicitly chosen task and interval. An optional stress
fixture adds overlapping, all-day, and overnight events. Existing board/timer checks
also run, including preserving picker selections across timer mutations.

## Board and editor pass

Board and list now share one task drawer with title, description, priority,
deadline, activity, starring, project movement, tags, dependencies, references,
and lifecycle actions. Save commits the draft and closes the drawer; Cancel or
Escape discards it. Immediate actions are labeled separately. Backend refreshes
do not replace an open draft, and stale revisions leave it intact with an error.
Focus enters the title field, Tab cycles inside the drawer, and closing returns
focus to the workspace.

The board has a compact toolbar, bounded scrolling, previous/next lane controls,
and a project-jump picker. Holding a dragged card near a viewport edge scrolls
toward offscreen destinations. Drop targets are clipped to the visible board;
Escape and pointer capture loss clear feedback and stop scrolling. Cards lift
with a short hover transition and cast a stronger shadow during inspection.

Settings keeps data operations prominent and groups organization maintenance
behind disclosures. Project/activity rows wrap rather than squeezing names and
actions into fixed columns. Search is shown on task/Today/history surfaces and
uses a session label in History.

The harness's large-board fixture has seven projects and a long lane. Tests
exercise edge scrolling, persisted offscreen moves, project jumping, draft survival
through backend notifications, draft saving, keyboard focus containment, and stale
revision rejection. Normal and minimum-width task drawers, Settings disclosures,
and History corrections were rendered and inspected.

## Final cross-screen audit

### Refined palette

The workspace now uses warm off-white, white input/card surfaces, dark slate text,
and deep teal for actions and selection. Muted amber and slate blue distinguish
paused and background sessions. Shared color tokens in `App.axaml` also configure
Fluent's input, dropdown, menu, disabled, focus, hover, and pressed resources.
The application explicitly requests the light variant so an OS dark preference
cannot introduce dark controls into the light workspace. This is a coherent light
theme, not a claim of a separately designed dark theme. Palette captures include
an open dropdown and focused/hovered/pressed controls in `artifacts/ui-palette`.

### Workspace verification

The final pass reviewed all workspace surfaces against the original direction:
tracker launchers and stateful clocks, a manipulable board, a focused Today view,
and a navigable time-based calendar. History and Settings retain their workflows;
Summary remains deliberately secondary. The reference informed the split tracker
layout and card feedback, without importing its implementation.

An always-open check found and fixed the frozen daily total: open intervals now
advance the display locally, while midnight triggers a workspace refresh. Shared
day boundaries use each midnight's UTC offset, with tests for 23-, 24-, and 25-hour
days. Task drafts are isolated from list rows so Cancel truly discards changes,
including when the same task is immediately reopened.

Verification: solution build with zero warnings/errors; 30 passing tests; clean
`git diff --check`; combined board, drawer, tracker, and calendar interaction checks
against disposable SQLite profiles. Final renders are in `artifacts/ui-final-verified`,
with minimum-size and empty states in `artifacts/ui-final-narrow` and
`artifacts/ui-final-empty`. Earlier passes also inspected expanded maintenance,
history correction, overnight/overlap schedules, and drag feedback. Native human
interaction, assistive-technology, and platform release checks remain separate work.
