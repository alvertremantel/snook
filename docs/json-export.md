# JSON workspace export

The current JSON export format is **schema 5**. `ExportJsonAsync` and the CLI
`call export-json` write the same UTF-8 JSON document in embedded and daemon
modes. The result contains its host-local path, byte count, SHA-256 and
`schemaVersion: 5`. Check the version before interpreting the file. Export is a
portable data report, not a SQLite backup or an import package; there is no JSON
restore/import command. Use verified database backups for lossless workspace
recovery, migration and preservation of retry receipts.

## Encoding and snapshot rules

- Top-level keys and the `habitTracking`/`journaling` section keys are camelCase.
  Domain and relationship record properties are PascalCase, as in earlier exports.
  This is distinct from the CLI/RPC's camelCase response encoding.
- UUIDs are strings; ID-to-tag maps use UUID property names and arrays of tag UUIDs.
  Missing relationships can be represented by an absent map key. Empty collections
  are arrays, and nullable record fields are explicit JSON `null`.
- Persisted UTC instants are ISO-8601 strings at SQLite's millisecond precision.
  `exportedAtUtc` is generation metadata and may have finer clock precision.
  Civil dates are `YYYY-MM-DD`; time-zone identifiers and recurrence rules remain
  explicit and are not converted to the exporting machine's local zone.
- Enums retain the earlier numeric encoding: task status 0=open, 1=completed;
  priority 0=none, 1=low, 2=medium, 3=high, 4=urgent; session lane 0=foreground,
  1=background; session state 0=running, 1=paused, 2=stopped, 3=recovery-required.
- The export holds the shared writer gate while collecting all sections. Its
  `committedCursor` identifies that snapshot's last committed mutation; it is not
  a durable SSE replay token and may decrease after database restore. Export does
  not advance the mutation cursor or create an entity operation receipt.
- Array order is not an identity contract. Join records by their IDs, not indexes.
  Unknown additional fields should be ignored only when a consumer understands
  the document's declared schema version.

## Sections

| Key | Contents |
| --- | --- |
| `schemaVersion`, `exportedAtUtc`, `committedCursor` | Format version, generation time and persisted snapshot cursor. |
| `workspace` | Workspace ID, name, creation instant and revision. |
| `settings` | Persisted `AllowConcurrentForeground` and `Revision`, not a host environment override. |
| `boards`, `projects`, `activityGroups`, `activities` | Domain records with organization, ordering, favorites, grouping and applicable archive/delete state. |
| `tasks` | Task IDs, parent project, text, status/priority, favorite, civil/instant due fields, default activity, completion/archive/delete state and revision. |
| `taskLinks` | All stored task-link records, including those belonging to archived/deleted tasks and stored link tombstones. |
| `taskDependencies` | All stored dependency edges, including edges involving archived/deleted tasks. |
| `tags` | All stored global tags with IDs, workspace ID, normalized/display names, color, revision and `DeletedAtUtc`. |
| `taskTagIds`, `projectTagIds`, `activityTagIds` | Stored tag assignments, including assignments whose parent is archived/deleted. |
| `calendars` | Calendar IDs, names, colors, visibility, revision and deletion state. |
| `scheduleBlocks` | Stored planning blocks, task/activity attribution, UTC ranges, time zones, recurrence and deletion state. |
| `calendarEvents`, `calendarEventExceptions` | Stored event series and occurrence overrides/cancellations; not an expanded calendar-range query. |
| `sessions` | All stored tracking sessions, including intervals, attribution, notes, recovery state, revisions and `DeletedAtUtc`. |
| `corrections` | Stored correction reason, before/after session snapshots, operation ID and application instant, including corrections for deleted sessions. |
| `habitTracking` | `habits` and `checkIns`, including archived/deleted habits and retained check-in history. |
| `journaling` | `journals` and `entries`, including deletion state, content, occurrence time, mood and independent journal-entry tags. |

The current command surface does not delete tags, links or tracking sessions,
but schema 5 preserves their stored deletion markers when present. Ordinary
active-screen queries must not be used as an export filter. Removed relationships
that were physically deleted from SQLite cannot be reconstructed by an export.

### Task links and dependencies

`taskLinks` records contain `Id`, `TaskId`, nullable `Label`, `Uri`, `Kind`,
`CreatedAtUtc`, nullable `DeletedAtUtc`, and `Revision`. A URI is data; reading an
export must not open links or execute their contents automatically.

`taskDependencies` records contain `Id`, `TaskId`, `PrerequisiteTaskId`,
`CreatedAtUtc`, and `Revision`. The direction is “TaskId requires
PrerequisiteTaskId,” not the reverse. Neither array is filtered by current task
visibility or completion state.

## Compatibility, privacy and operation

Schema 5 retains schema 4's sections and adds `settings`, `committedCursor`,
`taskLinks` and `taskDependencies`. It also includes stored deleted tags/sessions
and adds their `DeletedAtUtc` properties. Earlier schema 4 consumers must explicitly
accept the new version; do not silently claim older exports contained these fields.
This is an export format change, not a SQLite migration or a new RPC signature.

The document excludes daemon tokens/endpoints, local connection profiles,
ownership files, schema migration SQL, mutation logs and retry-receipt tables.
It is not a raw dump of storage-only metadata such as tag-assignment row IDs.
Exported user text, file URIs, journal entries and history are still private data.
The JSON document is not encrypted. On Unix it is published mode 0600; protect
the destination and any copied/shared versions.

Use a new absolute file name for every intentional export. Existing files and
protected workspace/credential paths are rejected, and symlink paths are not
supported. A lost response does not make repeating the call an exact receipt
replay. Inspect existing output and its hash before deciding to create another
artifact. See [operations](operations.md#backup-and-restore) for staging,
interrupted publication, cancellation and platform limitations.
