# Desktop UI review and revision plan

Date: 2026-09-16. Reviewed the actual seeded 1280×820 screens, drawer and expanded
states, empty/minimum 980×640 screens, shared styles, view models, and screenshot
interaction checks. Baseline captures: `/tmp/snook-modern-before` and
`/tmp/snook-modern-before-narrow`. The worktree was clean before this pass.

## Findings

| Surface | Finding | Revision |
| --- | --- | --- |
| Shell | Stable, compact navigation and coherent teal palette already work. Long slogan headings repeat the page's purpose. | Keep the shell; use shorter working titles on the revised pages. |
| Today | Useful capture/focus split and schedule preview. Manual entry is another disclosure below the fold. | Preserve layout; open manual entry as a focused editor. |
| Tasks | Three toolbar rows, lifecycle checkboxes competing with daily actions, inert completion glyph, and a whole second row for Edit. Board cards repeat the same action hierarchy. | Consolidate filters into a compact popup; put edit alongside direct actions; tighten card/row spacing and give work more room. |
| Tracker | The activity/session split and stateful clocks are good. Four page-wide expanders below it hide creation, manual entry, and duplicate maintenance. | Toolbar actions for manual time and new activity; a direct route to the activity library in Settings. Keep timers visible. |
| History | Each session is visually subordinate to its correction expander. Expanded editors consume most of the screen. | Compact ledger with separate duration/state columns and a single correction drawer. Preserve revision checks, draft ownership, and provenance. |
| Summary | A mostly empty card of undifferentiated text; grouping has no selected state; attributed and coverage values are hard to scan. | Selected grouping control, aligned duration/coverage columns, proportional attributed-time bars, and an honest explanation of overlapping time. |
| Settings | A form dump: category navigation is implemented as expanders, editable controls are always present inside, maintenance is duplicated in Tracker. At minimum size, nested borders and fixed form widths waste room. | Category navigation, readable organization/activity/calendar catalogs, explicit Edit actions, bounded editors, separate data/timer sections. Preserve create, reorder, tag, archive, delete and recovery workflows. |
| Calendar | Time grid and range navigation are sound. Creation and detailed editing still use broad disclosures. | Keep the date surface; use compact creation actions and show the detailed list in Agenda, with local editing actions. |
| Editors | Task drawer already has useful focus and conflict handling; maintenance forms lack equivalent draft isolation. | Reuse the drawer interaction grammar for maintenance, corrections, manual time, and creation. Cancel/Escape discard drafts; refresh cannot replace an open draft. |

## Implementation sequence

1. Establish shared compact toolbar, tab, ledger, and catalog styles; simplify Tasks.
2. Introduce a reusable bounded utility drawer with explicit Save/Cancel, local-time
   labels, focus containment/return, and isolated selected-record drafts.
3. Move Tracker/Today secondary actions into the drawer; rebuild Settings around
   category navigation and catalogs, keeping all existing capabilities reachable.
4. Rebuild History and Summary; replace Calendar's remaining page-wide disclosure
   structure without changing its time projection or navigation.
5. Extend the screenshot harness for the new states and meaningful persisted
   interactions. Review seeded and empty captures at both supported sizes, including
   drawer scrolling, long content, task drag/drop, timers, and calendar planning.
6. Run the pinned full solution build/test commands and `git diff --check`; record
   evidence and limitations in the design/progress documentation.

## Acceptance criteria

- No screen-wide expander navigation on the revised workspace pages.
- Daily actions are visible without scrolling past maintenance forms.
- Secondary forms have bounded width, explicit labels, and usable keyboard access.
- Existing mutations and recovery paths stay available; transport/backend contracts
  and the real workspace database are unchanged by this UI work.
- Drafts survive backend notifications; Cancel/Escape discard them; stale saves keep
  the draft visible with feedback.
- Rendered minimum-size and empty states are inspected, not merely generated.
- Headless interaction and persistence checks pass. Native human interaction and
  assistive-technology validation remain separate release work.

## Delivered revision and evidence

The plan is implemented. All page-wide expanders have been removed; the three
remaining disclosures are bounded secondary actions inside the task drawer.
The utility drawer covers record maintenance, corrections, manual time, new
activities, events, and task planning. Record edits retain their original revision
and independent picker snapshots. Tags are clearly marked as immediate actions.

Visual review includes seeded and empty states at 1280×820 and 980×640. Captures
are under `artifacts/ui-modernized*`; the baseline remains under
`/tmp/snook-modern-before*`. The full solution builds without warnings and all
33 tests pass. Headless interaction checks cover both new workflows and the
existing board, timer, calendar, and task-drawer behavior. See `docs/progress.md`
for the detailed verification record and `README.md` for reproducible switches.

Two interaction findings were fixed during implementation: refreshed picker
collections could disrupt a creation draft, and correcting notes on a sub-minute
session could round away its original interval boundaries. Picker snapshots now
belong to the editor; untouched correction timestamps retain their exact values.
