# Workspace redesign

## Current direction: September 2026 modernization

Task rows and cards provide selection checkboxes separate from completion controls.
Select all in view (also Ctrl+A outside text fields), Clear, and Edit selected are
available in both arrangements. Selected rows have a checked control and accent
border. Selection follows stable task IDs across refreshes and drops tasks hidden
by changed filters. The bulk drawer snapshots selected tasks and their revisions;
each editable field has an explicit opt-in checkbox. Blank checked due dates and
No activity clear those values; unchecked fields remain untouched. Tags support
add/remove. Saving is atomic across the selection, including moves and tags.
Conflicts preserve the draft and expose an explicit latest-revision review before
retry. The shared drawer contains focus, keeps Save/status reachable, and discards
drafts on Cancel/Escape.

Tasks and projects have direct star toggles and editor controls. The Starred scope
shows individually starred tasks plus tasks belonging to starred projects, with
favorite projects listed above; it respects board/search/lifecycle filters. Boards
have no favorite state. Due dates remain civil dates separate from planned blocks.
Event calendar Day/Week/Month headers offer a due-count popup for open, unarchived
tasks, with their project path and a direct action to open task details. Due tasks
never enter the time grid unless independently scheduled.

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
Navigation uses original vector icons in aligned 24-pixel slots beside the labels:
a sun, checklist, stopwatch, calendar, history clock, bar chart, and gear. Shared
rounded strokes and subtle foreground-tinted fills give the set consistent detail.
Icons inherit label colors for selection, hover, and press, and remain decorative
so the named navigation buttons own keyboard focus and accessibility labels.
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

### Event and History calendar modes

Calendar now separates the Event schedule from a History usage view. Event retains
Day/Week/Month/Agenda and its creation/editing actions. History exposes only Week
and Flex, with recorded task and activity intervals through now and subdued gray
task plans after now. It excludes calendar events and past plans. A plan spanning
now displays only its future remainder. Recorded intervals are independently split
at local midnight, so pauses remain gaps and overnight work belongs to each day.

History uses elapsed minutes since each local midnight: 23- and 25-hour days keep
correct duration proportions, with local hour/offset labels on the affected column.
It has a one-pixel minimum mark instead of Event's 20-minute minimum hit area;
short records retain keyboard controls, tooltips, and exact timestamp inspection.
Recorded foreground/background sessions and running intervals have explicit state
text in their inspection details and automation names. Daily totals sum interval
durations, including simultaneous sessions, as their tooltip explains.

Flex uses a 156-pixel minimum day width after the time gutter, bounded to 2–21 days,
and distributes remaining width evenly. The selected date stays near the center;
previous/next shifts by a full visible range. Mode-specific arrangements are kept
for the app session. Width changes are debounced, obsolete calendar loads are
discarded, and open history intervals advance locally every 15 seconds. Rebuilds
preserve the time scroll and keyboard focus on the same interval. The timeline
fits the remaining window height while keeping every hour reachable.

History reads all pages up to 10,000 sessions per range and explicitly reports
truncation. Both embedded and daemon clients use the existing shared history
contract. The separate History ledger remains the correction workflow.

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
