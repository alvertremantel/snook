# Workspace redesign

Android Settings currently offers **Review backup to restore**, distinct from the
desktop **Restore latest backup** action. The native file picker selects a bundle;
only backend-verified identity is displayed in the subsequent native review. Show
current and incoming workspace names/IDs, backup time and size. **Replace workspace**
explicitly warns about replacing all data and discarding drafts; **Keep current
workspace** cancels. Back,
backgrounding and rotation dismiss the activity-bound review; selection or
verification that outlives the activity must not reopen it automatically. The handoff
requires a resumed, unlocked activity and free-space checks, disables workspace
interaction during replacement, then rebuilds Today against the new session borrower.
Optional app lock now hides the activity before device-credential authentication,
publishes the workspace session's `Locked` state, and clears private notification
and widget projections until unlock. Restore success rebuilds timer, reminder and
widget projections only against the reopened workspace.

Compact board lanes size from the actual board viewport, capped at 360 dp, rather
than assuming the entire window is usable. Narrow project headers give names a
full row and move counts/star/More below. The existing project picker stretches;
previous/next controls follow its selected project and disable at the ends. Project
jumps change horizontal offset only. Desktop drag/drop, edge scrolling and task
move/editor behavior remain intact. Native touch/assistive acceptance is pending.

Android editor Back now uses a native discard confirmation. A keyboard-consumed
Back does not close the editor; Android Back keys are intercepted before the
Avalonia keyboard path can treat them as Escape. The activity's lifecycle-scoped
AndroidX callback handles system navigation, delegating unchanged when no editor
is open. Keyboard Escape and explicit Cancel retain their existing semantics.
Confirmation cancellation/rotation keeps the draft, and a stale confirmation
cannot discard a subsequently opened task or utility editor. This is not complete
predictive-gesture, popup-ordering or assistive-technology acceptance.

Calendar controls now adapt below 840 dp: range navigation sits below range/zone
labels, view choices and creation actions wrap, and selected-item actions sit below
inspection details. Narrow agenda rows separate task names, timing and actions;
event text wraps. Time-grid panes below 600 dp use 160 dp minimum day columns.
Overflowing multi-day ranges expose a 48 dp day picker and previous/next-day actions
with disabled end states. Hour labels remain pinned while columns pan; day selection
survives data rebuilds within the same range.

The event Month view preserves the complete seven-column overview at 600 dp and
above. Below 600 dp it presents one week at a time: a labeled week picker and 48 dp
previous/next actions sit above seven full-width day cards in the page scroll. Each
card keeps its complete date, due-task popup and inspectable scheduled items; empty
days say that they have no scheduled items. The current/selected week survives
collection rebuilds and crossing the compact threshold. This avoids unreadable
seven-column phone cells without hiding any week. Native assistive-technology and
physical-device acceptance remain pending.

Habits adapts below 640 dp of pane width. New habit moves below the introduction;
the history date input spans the pane with View date/Today beneath. Habit names and
state use a full row, followed by labeled Edit/More actions. Existing seven-day
controls wrap without shrinking their touch targets, and archive/delete filters
gain 48 dp minimum height. Width changes preserve the unapplied history-date input.
Recurrence, check/undo and earlier-day correction semantics are unchanged.

Journals adapts to its available pane width below 640 dp: the journal selector
stretches above wrappable creation actions; journal settings moves below context;
entry search occupies a full row, with tag filtering and Apply beneath. Dates and
empty-state headings wrap, and entry read/delete actions can wrap. Wider panes
restore the original toolbar/filter columns. The same controls and unapplied filter
text remain in place throughout resizing. Journal definitions and journal-entry
drafts use the encrypted utility-editor recovery described below; complete native
assistive-technology acceptance remains external.

History and Summary adapt below 840 dp. History gives work/context/state a full
row, then shows date, labeled duration and a 48 dp Edit action beneath it; manual
time moves below the section heading. Summary gives group names and bars the full
row, followed by explicitly labeled attributed-time and clock-coverage values.
Desktop column headings return at expanded widths. Empty text wraps, and existing
pagination, correction commands and aggregation semantics are unchanged. Report
column definitions remain stable during resize to avoid invalidating a Grid measure
pass while cells still refer to the previous columns.

The timer workspace stacks below 840 dp: in-progress focus/background sessions
come before the activity library, using the page's main scroll rather than two
height-limited nested columns. Manual time and new activity share a second toolbar
row, and timer state moves below its title. At expanded width, the independent
activity/session columns return. Existing controls and manual-time drafts survive
width changes; timer commands and persisted elapsed-time behavior are unchanged.

Android Settings contains a dedicated **Timer notifications** card. Its explanation
precedes the runtime permission request, says that denial does not affect in-app
timers, and discloses that task/activity details are hidden from the public lock-
screen surface. The ordinary low-importance notification shows committed timer
state, a system chronometer for running sessions, and labeled Pause/Resume and Stop
actions. Tapping its body opens Time tracker. A paused timer uses static duration
text; Stop removes its native surface after the shared backend commit. Denial or a
disabled channel produces status text rather than repeated prompts or a timer error.
On Android 16 and newer, **Review Live Update access** checks current promotion
access and otherwise hands off to the app-specific Android settings screen when the
device supplies it. If the destination is absent, the inline status explains that
ordinary notifications remain available. One running timer may request promotion;
paused timers and summaries do not. Promotion never changes the content, actions or
chronometer fallback. Custom artwork, actual promoted/demoted presentation,
notification-shade accessibility and physical-device acceptance remain open.

Android clean install now starts on a single host-choice page without opening a
backend. The choices explain that independent and attached workspaces do not sync
or merge. **Independent workspace** requires a native foreground confirmation that
states the offline/backup consequences; cancellation returns to the choice without
creating a database. **Connect to a Snook daemon** accepts only a canonical HTTPS
`*.ts.net` Serve origin and a short-lived pairing code, then requires review of the
observed identity before storing a Keystore-protected per-install credential. It
never creates a local fallback. After either confirmed choice, a compact bar
identifies the active host, with development probes behind Tools. Existing
development databases are adopted during upgrade, and the confirmed choice survives
process restart. Returning from probes preserves the process-owned workspace and
drafts. A normal explicit component launch with no action/data is treated as a
launch, not an invalid share. Actual Serve/ACL/roaming/DERP and independent
security-review acceptance remain external gates.

The shared quick-capture model has an explicit recovery boundary: a bounded,
versioned snapshot binds the project/title and pending creation request to one
workspace, with a seven-day expiry. Restoring does not submit a mutation and cannot
replace another capture or editor. If the previous create committed but its response
was lost, an unchanged retry reuses the original receipt identity.

Android now wires this quick-capture boundary to a private Keystore-encrypted file
under no-backup storage. Edits checkpoint after 400 ms and on backgrounding; a
creation's exact request is checkpointed before dispatch. Writes are serialized and
replace the file through a flushed staging file. Only capture is restored, after
loading the workspace's current projects, with a visible recovery notice. An
unavailable key/corrupt file or a missing project does not silently overwrite the
recovery copy: the host offers explicit discard. Dismissing a successful recovery
notice leaves the current capture intact; clearing its title deletes its saved
copy.

An open task editor has a separate bounded, workspace-bound encrypted recovery
copy. It contains its unsaved title, description, priority, deadline, activity,
star, move target and currently entered tag/dependency/link fields, plus the task ID
and expected revision. Restoration first reloads the authoritative task, opens the
normal editor, overlays those inputs and asks for review; it never invokes Save. The
saved expected revision is retained, so an intervening task update reaches the
normal actionable conflict path instead of being overwritten. Cancel or a completed
Save deletes the recovery copy. The active utility editor has its own bounded 64 KiB,
workspace-bound Keystore envelope covering manual time, activity/planning/event
creation, organization/calendar/history editing, journals, habits and batch tasks.
Sensitive journal text remains private; uncertain creates/saves retain their exact
request, and batch recovery retains each task revision. Utility inputs are sampled
once per second while open and flushed on backgrounding; unchanged samples do not
rewrite ciphertext. The newest uncheckpointed keystrokes can still be lost.

Android process recreation also restores bounded navigation context independently
of drafts. A workspace-bound private no-backup snapshot records the current
destination, task list/board and sort selection, optional board, calendar mode/view/
date, summary grouping, Settings category and optional journal. It deliberately
excludes search/filter text, task capture, editor content and mutation identity.
Navigation checkpoints after 250 ms and on backgrounding. Missing workspace records
fall back to the normal all-board/all-journal state; wrong-workspace or malformed
state is ignored. If a task editor is recovered, its Tasks destination takes
precedence over the earlier page.

## Current direction: September 2026 modernization

The workspace content now lives in `WorkspaceView`, a shared `UserControl`, while
`MainWindow` supplies the desktop lifetime, connection dialog and screenshot host.
The host/session owns `MainWindowViewModel` and its backend; detaching or replacing
a view does not initialize/dispose the session or discard its drafts. View-local
subscriptions, resize timers, pointer capture and host-key routing detach with the
view. Escape remains available when a removed control leaves focus on the host.
Task/project template ancestor bindings target `WorkspaceView`, not `Window`.
The mixed binding policy is unchanged: the workspace root disables compiled
bindings and typed templates opt in individually. The Android host borrows one
process-owned model and backend across activity views. Subsequent adaptive passes
replaced the permanent compact sidebar, converted dense feature layouts, and retain
explicit automation names; native assistive-technology acceptance remains open.

The first adaptive pass now uses current view width: below 600 dp the sidebar is
hidden, Today/Tasks/Timer/More stay at the bottom, and More exposes all remaining
destinations. Today panes stack, search sits below the heading, and open drawers
fill the compact width without replacing their draft controls. Subtitle space is
suppressed at compact width or below 480 dp height. Above the compact breakpoint,
the medium rail and expanded navigation described below take over. Feature-specific
compact layouts cover Today, Tasks/boards, Timer, Calendar, History, Summary,
Journals, Habits and Settings. Safe-area padding and IME resize are owned by the
Android top level. Physical-device, large-font and native assistive-technology
acceptance remain outstanding rather than unimplemented UI.

Compact Today now follows capture → timers → next tasks → agenda, with the same
control tree retained across width changes. The wide layout uses two aligned card
rows. Compact next-task Start/Done actions sit below the task text instead of
competing with its width, and Stop is a visible text action on Today timers.
Compact buttons have a 48 dp minimum width and height; this is not a blanket
touch-target or accessibility acceptance claim for other controls or pages.

Compact Tasks now places board creation on a separate toolbar row and gives quick
capture a full-width title above its project picker and Add action. The same capture
controls survive width changes. Task list text occupies the primary row, with
priority/star/start/edit below it; project/time metadata wraps. Board tabs retain
their explicit previous/next controls. The creation form width is bounded by the
available view width. Board lane sizing, short-height/IME popup reachability and
remaining editor fields still need mobile acceptance work.

At 600–839 dp, navigation now becomes an 88 dp rail with visible icon/label pairs
and 64 dp buttons. It reuses all nine destinations and scrolls vertically rather
than hiding lower items. Selected text is dark on its light selected background.
The workspace host footer remains visible; the full sidebar timer card is hidden
on the rail. Today and Tasks retain stacked content in this width class. At 840 dp
the expanded sidebar returns. Width changes retain capture/editor ownership.
This is window-width behavior, not a tablet device flag or physical-tablet signoff.

Android visibility is separate from view ownership. Started activities contribute
to foreground visibility; selecting the platform-probe page also suspends workspace
presentation. Hiding stops the display timer and skips new backend-driven refreshes;
return requests a fresh snapshot while preserving editor/capture state. A visibility
generation rejects late bootstrap results from an earlier visibility period.
The session/SQLite owner is not closed, and persisted timer truth is not paused.
Already-running page requests or mutations are not cancelled by this first pass;
broader cancellation/coalescing and native battery/process-eviction checks remain.

Page-load generations now invalidate Tasks, Today, History, Summary, Calendar,
Habits and journal catalog/entries when visibility changes or the model is disposed.
Obsolete reads stop before publishing or issuing the next request in a read chain.
History/journal pagination tokens are reset at that boundary. Today stages its
agenda/recent rows until its reads complete; History retains old rows until a valid
replacement is ready. This does not cancel a transport request already in progress
and does not interfere with mutation receipts or editor-owned drafts.

Primary presentation reads now share a visibility-scoped cancellation token.
Hiding or disposal cancels that scope; resume creates a fresh one. Navigation
loaders consume expected cancellation rather than letting it escape an async
command, and calendar startup/reload uses the same read path. Generation checks
remain necessary for backends that ignore cancellation. Writes never receive this
presentation token: hiding neither cancels nor replays a creation/save response.
The refresh-time manual task picker also uses the token and checks its presentation
generation before publishing options or continuing the refresh after its read.
Full refresh requests now coalesce per visibility generation: one running refresh
and one pending trailing refresh, using current filters when it runs. Callers wait
for the batch to settle. Hiding/disposal drops pending old-generation work; a fresh
resume never waits for an obsolete non-cooperative read. Individual page navigation
reads and editor-specific reads are not included in this coalescing boundary.

The Android development header now offers review/discard for one incoming plain-text
task share. Receiving it does not save a task. Review selects the independent
workspace and fills quick capture only if no capture or
task/utility editor draft is present. A blocked review keeps the incoming share;
discard affects only that incoming text. Inputs are bounded to 300 characters,
expire after ten minutes, and remain in process memory only. URI payloads and
attachments are rejected; a URL inside plain text is inert title text. Cold and warm
activity entry now share one bounded classifier for normal launch, exactly shaped
plain-text share and authenticated timer-content open. Unknown actions, launcher
payloads, extra keys, ClipData, stream/URI substitution and mismatched timer routes
show rejection status without navigating, saving or replacing the current draft.
The timer coordinator still resolves its opaque record and validates current
workspace/session state as a second gate. Long-text and direct journal Sharesheet
input remain out of scope. Four manifest shortcuts and four responsive widget
surfaces route through the same validated session boundary; locked, stale and
unavailable state renders privacy-safe placeholders.

Connection setup is a separate startup window, also available before opening any
workspace with `--configure`. It has explicit **Embedded workspace**, **Local
daemon**, and **Tailnet daemon** choices, persistent field labels, policy-specific
endpoint/token/pairing hints, a scrolling form and fixed
status/Connect/Cancel actions at 620×600. Failure keeps the draft visible for Retry;
Cancel/Escape drains pending connection work and suppresses late window opening.
The optional Remember checkbox saves device-local settings, never credentials.

Tailnet mode never asks for the server data directory. It explains that Tailscale
must be installed/signed in separately and that Funnel is public and unsupported.
The user enters a canonical Serve URL, device name and single-use code. Redemption
does not immediately save: the same window displays workspace/server IDs and
contract for review, then exposes a separate confirmation action. Cancel revokes
the just-issued credential. Confirmation prefers the named native protected store;
an owner-only file requires an explicit checkbox, and only the resulting reference
is saved. A saved profile card shows the Serve host, pinned server/workspace IDs,
last saved contract negotiation, credential label, and the fact that identity is
reverified on every connection. Settings on a locally connected daemon provide invitation
creation, optional intended-login restriction, bounded device inventory and a
textual Revoke action. Invitation fields keep persistent visible labels, and an
armed invitation exposes a visible Cancel action that revokes it early and clears
the displayed payload. These controls have automation names and remain scroll
reachable at minimum size.

The workspace footer identifies embedded mode or the actual daemon endpoint and
offers **Connection…**. That modal saves settings for the next launch, without
switching the current backend or discarding editor drafts. Validation preserves
entered values; Escape discards them and returns focus. An interrupted daemon
connection has a textual stale-data warning and Retry refresh, separate from
ordinary mutation status. Reconnecting to the same host preserves existing drafts;
there is no automatic embedded fallback or offline mutation queue.

Create actions own an operation request alongside their draft. A lost daemon
response leaves the draft and exact request available for an explicit unchanged
retry. Derived activity defaults, relative times and local-to-UTC interpretation
are captured only once. Success clears the attempt before refreshing projections;
a failed refresh must not invite a duplicate create. Edits typed during an
in-flight save survive its response. Cancel/Escape discards the local attempt,
and a late response cannot close or replace a newly opened creation drawer.
Cancellation is not undo for a write already sent; reconcile it through refresh.
Creation retry state is in-memory and is not an offline queue or persisted draft.

Restored timers appear as Dashboard recovery cards with a task/activity label,
lane, start time, frozen duration, exact last-known boundary (including UTC offset)
and contextual automation names. Wrapped explanations distinguish
the choices: Last known retains frozen time, Stop now credits the gap, and Continue
starts now without the gap. Actions sit below the explanation and remain reachable
through the page scroll at 980×640. Unchanged recovery rows retain their controls
across refresh so keyboard focus is not discarded. The backend records both the
restore preparation and explicit decision as before/after correction provenance.

Habits is a workspace-wide daily yes/no tracker beside Tasks. Its cards expose
seven direct day buttons with Done/Open text, a bounded disclosure for the rest
of a 30-day history, and date navigation for older corrections. Current streaks
remain current while reviewing old history; completion counts describe the visible
range. Archive/delete hide routines without losing history, and explicit filters
reveal restore actions. Habits do not create tasks, calendar blocks, or timers.

Creation/editing uses the shared utility drawer. Start date and creation time zone
stay fixed after creation; names and descriptions are editable. Refreshes retain
the draft, stale saves offer a latest-saved comparison, and Cancel/Escape discard
the draft with focus returned. Day controls keep identity across refreshes so a
keyboard user can check or undo without losing their place. Days before the start,
future days, and corrections older than 366 days are disabled. The page refreshes
at a habit's own midnight even if the host and habit have different time zones.

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

## Journals

Journals is a first-level sidebar destination with an original notebook icon.
The page uses the workspace's warm surfaces and teal accents, a compact journal
picker, and chronological entry cards with generous line spacing. Each card
shows its journal, local date/time, title, excerpt, textual mood rating, and
journal-only tags. Exact tag and text filters are explicit; earlier entries load
in bounded pages. Empty states lead to journal creation and first-entry writing.

The writing drawer owns its title, multiline body, journal, local timestamp,
optional 1–7 mood, and comma-separated tag draft. Choosing a different journal
moves the entry on Save. An unset mood is labeled Not rated. Ratings always have
text, so their meaning does not depend on color. Content is plain text.
Backend refreshes preserve drafts; stale saves keep them open. Review latest
shows saved fields alongside the unchanged draft before a new revision can be
saved. Cancel and Escape discard edits, Tab cycles in the drawer, and focus
returns to its opener. Metadata and conflict details stay reachable by scrolling
at the 980×640 minimum size.

Journal settings contains rename and soft-delete/restore actions. Show deleted
exposes deleted entries and journals with explicit state text; entries remain
readable with a disabled Save until restored. Deleting a journal preserves its
contents and their individual deleted states. Journal tags are edited with the
entry draft and never appear in task or time-tracking tag controls.

## RSS reader

RSS is a stable desktop navigation item and an Android More destination. Keep the
subscription form in an Add subscription disclosure so reading gets the available
space. Filters, feed actions, and article actions wrap with 48 dp buttons; content
uses the shared page scroll. Articles expose explicit read and starred text, cached
plain-text expansion, and a separate system-browser action. Refresh preserves the
URL draft and existing article row identity. Delete is reversible through Show
deleted. Android navigation restoration recognizes RSS; URL entry remains an
in-process draft and does not claim encrypted editor recovery.
