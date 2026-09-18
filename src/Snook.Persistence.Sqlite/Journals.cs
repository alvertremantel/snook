using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Snook.Domain;

namespace Snook.Persistence.Sqlite;

public sealed record StoreJournalResult<T>(T Value, long? Cursor, Guid Id, long Revision, string Kind, DateTimeOffset CommittedAtUtc);
public sealed record StoreJournalEntryPage(IReadOnlyList<JournalEntry> Items, string? ContinuationToken, bool HasMore);

public sealed partial class SqliteStore
{
    private const string JournalsSql = """
        CREATE TABLE journals(
            id TEXT PRIMARY KEY, name TEXT NOT NULL, description TEXT NOT NULL,
            created_at_utc_ms INTEGER NOT NULL, deleted_at_utc_ms INTEGER, revision INTEGER NOT NULL CHECK(revision>0));
        CREATE TABLE journal_entries(
            id TEXT PRIMARY KEY, journal_id TEXT NOT NULL REFERENCES journals(id), title TEXT NOT NULL, content TEXT NOT NULL,
            occurred_at_utc_ms INTEGER NOT NULL, mood INTEGER CHECK(mood BETWEEN 1 AND 7),
            created_at_utc_ms INTEGER NOT NULL, updated_at_utc_ms INTEGER NOT NULL, deleted_at_utc_ms INTEGER,
            revision INTEGER NOT NULL CHECK(revision>0));
        CREATE INDEX journal_entries_order ON journal_entries(occurred_at_utc_ms DESC,id DESC);
        CREATE INDEX journal_entries_journal ON journal_entries(journal_id,occurred_at_utc_ms DESC,id DESC);
        CREATE TABLE journal_entry_tags(
            entry_id TEXT NOT NULL REFERENCES journal_entries(id), normalized_name TEXT NOT NULL, display_name TEXT NOT NULL,
            PRIMARY KEY(entry_id,normalized_name));
        CREATE INDEX journal_tags_name ON journal_entry_tags(normalized_name,entry_id);
        """;
    private static readonly string JournalsChecksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JournalsSql))).ToLowerInvariant();
    private const string EntrySelect = """
        SELECT e.id,e.journal_id,e.title,e.content,e.occurred_at_utc_ms,e.mood,e.created_at_utc_ms,e.updated_at_utc_ms,e.deleted_at_utc_ms,e.revision,
            (SELECT json_group_array(display_name) FROM (SELECT display_name FROM journal_entry_tags WHERE entry_id=e.id ORDER BY normalized_name))
        FROM journal_entries e JOIN journals j ON j.id=e.journal_id
        """;

    private static async Task EnsureJournalsMigrationAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.CommandText = "SELECT MAX(sequence) FROM schema_migrations;";
        if (Convert.ToInt32(await query.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) >= 9) return;
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteMigrationCommandAsync(connection, transaction, JournalsSql, cancellationToken);
        await ExecuteMigrationCommandAsync(connection, transaction,
            "INSERT INTO schema_migrations VALUES(9,'journals',$checksum,$now);", cancellationToken,
            ("$checksum", JournalsChecksum), ("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Journal>> GetJournalsAsync(bool includeDeleted, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await ReadJournalsAsync(connection, null, includeDeleted, cancellationToken);
    }

    private static async Task<IReadOnlyList<Journal>> ReadJournalsAsync(SqliteConnection connection, SqliteTransaction? transaction,
        bool includeDeleted, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id,name,description,created_at_utc_ms,deleted_at_utc_ms,revision FROM journals WHERE $deleted OR deleted_at_utc_ms IS NULL ORDER BY name COLLATE NOCASE,id LIMIT 201;";
        command.Parameters.AddWithValue("$deleted", includeDeleted);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<Journal>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadJournal(reader));
        if (result.Count > JournalRules.MaximumJournals) throw JournalInvalid("The journal catalog exceeds the limit of 200 journals.");
        return result;
    }

    public Task<StoreJournalResult<Journal>> SaveJournalAsync(Guid? journalId, JournalDefinition definition, long? expectedRevision,
        Guid operationId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var clean = JournalRules.Validate(definition);
        ValidateJournalIdRevision(journalId, expectedRevision);
        return WriteJournalAsync(operationId, "journal", new { journalId, definition, expectedRevision }, async (connection, transaction) =>
        {
            Journal journal;
            if (journalId is { } id)
            {
                journal = await ReadJournalAsync(connection, transaction, id, cancellationToken);
                CheckJournalRevision(journal.Revision, expectedRevision);
                EnsureJournalActive(journal);
                journal = journal with { Name = clean.Name, Description = clean.Description, Revision = journal.Revision + 1 };
                await ExecuteMigrationCommandAsync(connection, transaction,
                    "UPDATE journals SET name=$name,description=$description,revision=$revision WHERE id=$id;", cancellationToken,
                    ("$name", journal.Name), ("$description", journal.Description), ("$revision", journal.Revision), ("$id", Id(id)));
            }
            else
            {
                await using var count = connection.CreateCommand();
                count.Transaction = transaction;
                count.CommandText = "SELECT COUNT(*) FROM journals;";
                if (Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) >= JournalRules.MaximumJournals)
                    throw JournalInvalid("This workspace has reached 200 journals, including deleted journals.");
                journal = new Journal(Guid.NewGuid(), clean.Name, clean.Description, MillisecondUtc(now), null, 1);
                await ExecuteMigrationCommandAsync(connection, transaction,
                    "INSERT INTO journals VALUES($id,$name,$description,$now,NULL,1);", cancellationToken,
                    ("$id", Id(journal.Id)), ("$name", journal.Name), ("$description", journal.Description), ("$now", now.ToUnixTimeMilliseconds()));
            }
            return (journal, journal.Id, journal.Revision, journalId is null ? "created" : "updated");
        }, now, cancellationToken);
    }

    public Task<StoreJournalResult<Journal>> SetJournalDeletedAsync(Guid journalId, bool deleted, long expectedRevision,
        Guid operationId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ValidateJournalIdRevision(journalId, expectedRevision);
        return WriteJournalAsync(operationId, "journal", new { journalId, deleted, expectedRevision }, async (connection, transaction) =>
        {
            var journal = await ReadJournalAsync(connection, transaction, journalId, cancellationToken);
            CheckJournalRevision(journal.Revision, expectedRevision);
            journal = journal with { DeletedAtUtc = deleted ? journal.DeletedAtUtc ?? MillisecondUtc(now) : null, Revision = journal.Revision + 1 };
            await ExecuteMigrationCommandAsync(connection, transaction,
                "UPDATE journals SET deleted_at_utc_ms=$deleted,revision=$revision WHERE id=$id;", cancellationToken,
                ("$deleted", DbInstant(journal.DeletedAtUtc)), ("$revision", journal.Revision), ("$id", Id(journalId)));
            return (journal, journal.Id, journal.Revision, deleted ? "deleted" : "restored-deleted");
        }, now, cancellationToken);
    }

    public async Task<StoreJournalEntryPage> GetJournalEntriesAsync(Guid? journalId, string? search, string? tag, bool includeDeleted,
        int pageSize, string? continuationToken, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        if (journalId == Guid.Empty || search?.Length > 200 || tag?.Length > 50 || pageSize is < 1 or > 100 || continuationToken?.Length > 2048)
            throw JournalInvalid("Use a valid journal ID, search up to 200 characters, tag up to 50 characters, and page size from 1 to 100.");
        search = search?.Trim() ?? string.Empty;
        tag = tag?.Trim().ToUpperInvariant() ?? string.Empty;
        var scope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { journalId, search, tag, includeDeleted }))));
        JournalCursor? cursor = null;
        if (continuationToken is not null)
        {
            try { cursor = JsonSerializer.Deserialize<JournalCursor>(Convert.FromBase64String(continuationToken)); }
            catch (Exception exception) when (exception is JsonException or FormatException) { throw JournalInvalid("The journal continuation token is invalid."); }
            if (cursor is null || cursor.Scope != scope || cursor.Id == Guid.Empty) throw JournalInvalid("The continuation token does not match this journal query.");
        }
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = EntrySelect + """

            WHERE ($journal IS NULL OR e.journal_id=$journal)
              AND ($deleted OR (e.deleted_at_utc_ms IS NULL AND j.deleted_at_utc_ms IS NULL))
              AND ($search='' OR instr(lower(e.title || char(10) || e.content),lower($search))>0)
              AND ($tag='' OR EXISTS(SELECT 1 FROM journal_entry_tags t WHERE t.entry_id=e.id AND t.normalized_name=$tag))
              AND ($after IS NULL OR e.occurred_at_utc_ms<$after OR (e.occurred_at_utc_ms=$after AND e.id<$id))
            ORDER BY e.occurred_at_utc_ms DESC,e.id DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$journal", journalId is { } selected ? Id(selected) : DBNull.Value);
        command.Parameters.AddWithValue("$deleted", includeDeleted);
        command.Parameters.AddWithValue("$search", search);
        command.Parameters.AddWithValue("$tag", tag);
        command.Parameters.AddWithValue("$after", cursor is null ? DBNull.Value : cursor.Time);
        command.Parameters.AddWithValue("$id", cursor is null ? string.Empty : Id(cursor.Id));
        command.Parameters.AddWithValue("$limit", pageSize + 1);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var entries = new List<JournalEntry>();
        while (await reader.ReadAsync(cancellationToken)) entries.Add(ReadJournalEntry(reader));
        var hasMore = entries.Count > pageSize;
        if (hasMore) entries.RemoveAt(pageSize);
        var next = hasMore ? Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new JournalCursor(scope, entries[^1].OccurredAtUtc.ToUnixTimeMilliseconds(), entries[^1].Id))) : null;
        return new StoreJournalEntryPage(entries, next, hasMore);
    }

    public async Task<JournalEntry> GetJournalEntryAsync(Guid entryId, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        if (entryId == Guid.Empty) throw JournalInvalid("An entry ID is required.");
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await ReadJournalEntryAsync(connection, null, entryId, cancellationToken);
    }

    public async Task<IReadOnlyList<string>> GetJournalTagsAsync(Guid? journalId, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        if (journalId == Guid.Empty) throw JournalInvalid("Use a valid journal ID.");
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT MIN(t.display_name) FROM journal_entry_tags t JOIN journal_entries e ON e.id=t.entry_id JOIN journals j ON j.id=e.journal_id
            WHERE e.deleted_at_utc_ms IS NULL AND j.deleted_at_utc_ms IS NULL AND ($journal IS NULL OR e.journal_id=$journal)
            GROUP BY t.normalized_name ORDER BY t.normalized_name LIMIT 1001;
            """;
        command.Parameters.AddWithValue("$journal", journalId is { } id ? Id(id) : DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var tags = new List<string>();
        while (await reader.ReadAsync(cancellationToken)) tags.Add(reader.GetString(0));
        if (tags.Count > 1000) throw JournalInvalid("More than 1000 journal tags match. Read tags from a single journal or from paginated entries.");
        return tags;
    }

    public Task<StoreJournalResult<JournalEntry>> SaveJournalEntryAsync(Guid? entryId, JournalEntryDefinition definition, long? expectedRevision,
        Guid operationId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var clean = JournalRules.Validate(definition);
        ValidateJournalIdRevision(entryId, expectedRevision);
        return WriteJournalAsync(operationId, "journal-entry", new { entryId, definition, expectedRevision }, async (connection, transaction) =>
        {
            EnsureJournalActive(await ReadJournalAsync(connection, transaction, clean.JournalId, cancellationToken));
            var entry = new JournalEntry(Guid.NewGuid(), clean.JournalId, clean.Title, clean.Content, clean.OccurredAtUtc, clean.Mood,
                clean.Tags!, MillisecondUtc(now), MillisecondUtc(now), null, 1);
            if (entryId is { } id)
            {
                var before = await ReadJournalEntryAsync(connection, transaction, id, cancellationToken);
                CheckJournalRevision(before.Revision, expectedRevision);
                EnsureJournalActive(await ReadJournalAsync(connection, transaction, before.JournalId, cancellationToken));
                if (before.DeletedAtUtc is not null) throw new SnookException(SnookErrorCode.InvalidTransition, "Restore the entry before editing it.");
                entry = entry with { Id = id, CreatedAtUtc = before.CreatedAtUtc, Revision = before.Revision + 1 };
            }
            await ExecuteMigrationCommandAsync(connection, transaction, """
                INSERT INTO journal_entries VALUES($id,$journal,$title,$content,$occurred,$mood,$created,$updated,NULL,$revision)
                ON CONFLICT(id) DO UPDATE SET journal_id=$journal,title=$title,content=$content,occurred_at_utc_ms=$occurred,
                    mood=$mood,updated_at_utc_ms=$updated,revision=$revision;
                """, cancellationToken, ("$id", Id(entry.Id)), ("$journal", Id(entry.JournalId)), ("$title", entry.Title), ("$content", entry.Content),
                ("$occurred", entry.OccurredAtUtc.ToUnixTimeMilliseconds()), ("$mood", (object?)entry.Mood ?? DBNull.Value),
                ("$created", entry.CreatedAtUtc.ToUnixTimeMilliseconds()), ("$updated", entry.UpdatedAtUtc.ToUnixTimeMilliseconds()), ("$revision", entry.Revision));
            await ExecuteMigrationCommandAsync(connection, transaction, "DELETE FROM journal_entry_tags WHERE entry_id=$id;", cancellationToken, ("$id", Id(entry.Id)));
            foreach (var tag in entry.Tags)
                await ExecuteMigrationCommandAsync(connection, transaction, "INSERT INTO journal_entry_tags VALUES($id,$normalized,$display);", cancellationToken,
                    ("$id", Id(entry.Id)), ("$normalized", tag.ToUpperInvariant()), ("$display", tag));
            return (entry, entry.Id, entry.Revision, entryId is null ? "created" : "updated");
        }, now, cancellationToken);
    }

    public Task<StoreJournalResult<JournalEntry>> SetJournalEntryDeletedAsync(Guid entryId, bool deleted, long expectedRevision,
        Guid operationId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ValidateJournalIdRevision(entryId, expectedRevision);
        return WriteJournalAsync(operationId, "journal-entry", new { entryId, deleted, expectedRevision }, async (connection, transaction) =>
        {
            var entry = await ReadJournalEntryAsync(connection, transaction, entryId, cancellationToken);
            CheckJournalRevision(entry.Revision, expectedRevision);
            EnsureJournalActive(await ReadJournalAsync(connection, transaction, entry.JournalId, cancellationToken));
            entry = entry with { DeletedAtUtc = deleted ? entry.DeletedAtUtc ?? MillisecondUtc(now) : null, UpdatedAtUtc = MillisecondUtc(now), Revision = entry.Revision + 1 };
            await ExecuteMigrationCommandAsync(connection, transaction,
                "UPDATE journal_entries SET deleted_at_utc_ms=$deleted,updated_at_utc_ms=$now,revision=$revision WHERE id=$id;", cancellationToken,
                ("$deleted", DbInstant(entry.DeletedAtUtc)), ("$now", entry.UpdatedAtUtc.ToUnixTimeMilliseconds()), ("$revision", entry.Revision), ("$id", Id(entryId)));
            return (entry, entry.Id, entry.Revision, deleted ? "deleted" : "restored-deleted");
        }, now, cancellationToken);
    }

    private Task<StoreJournalResult<T>> WriteJournalAsync<T>(Guid operationId, string aggregate, object arguments,
        Func<SqliteConnection, SqliteTransaction, Task<(T Value, Guid Id, long Revision, string Kind)>> change,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        EnsureInitialized();
        if (operationId == Guid.Empty) throw JournalInvalid("An operation ID is required.");
        var fingerprint = JsonSerializer.Serialize(new { aggregate, arguments });
        return WriteAsync(async (connection, transaction) =>
        {
            await using var receipt = connection.CreateCommand();
            receipt.Transaction = transaction;
            receipt.CommandText = "SELECT result_payload FROM operation_receipts WHERE operation_id=$id;";
            receipt.Parameters.AddWithValue("$id", Id(operationId));
            var payload = await receipt.ExecuteScalarAsync(cancellationToken);
            if (payload is not null)
            {
                JournalReceipt<T>? saved = null;
                if (payload is string json)
                {
                    try { saved = JsonSerializer.Deserialize<JournalReceipt<T>>(json); }
                    catch (JsonException) { /* A different operation owns this receipt. */ }
                }
                if (saved?.Fingerprint != fingerprint || saved.Result is null)
                    throw JournalInvalid("This operation ID was already used for a different request.");
                return saved.Result with { Cursor = null };
            }
            var (value, id, revision, kind) = await change(connection, transaction);
            await RecordMutationAsync(connection, transaction, operationId, aggregate, id, kind, revision - 1, revision, now, cancellationToken);
            await using var cursorQuery = connection.CreateCommand();
            cursorQuery.Transaction = transaction;
            cursorQuery.CommandText = "SELECT last_insert_rowid();";
            var cursor = Convert.ToInt64(await cursorQuery.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
            var result = new StoreJournalResult<T>(value, cursor, id, revision, kind, MillisecondUtc(now));
            await RecordReceiptAsync(connection, transaction, operationId, id, revision, cancellationToken);
            await ExecuteMigrationCommandAsync(connection, transaction, "UPDATE operation_receipts SET result_payload=$payload WHERE operation_id=$id;", cancellationToken,
                ("$payload", JsonSerializer.Serialize(new JournalReceipt<T>(fingerprint, result))), ("$id", Id(operationId)));
            return result;
        }, cancellationToken);
    }

    private static async Task<Journal> ReadJournalAsync(SqliteConnection connection, SqliteTransaction? transaction, Guid id, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id,name,description,created_at_utc_ms,deleted_at_utc_ms,revision FROM journals WHERE id=$id;";
        command.Parameters.AddWithValue("$id", Id(id));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadJournal(reader) : throw new SnookException(SnookErrorCode.NotFound, "The journal does not exist.");
    }

    private static Journal ReadJournal(SqliteDataReader reader) => new(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2),
        FromMs(reader.GetInt64(3)), NullableMs(reader, 4), reader.GetInt64(5));

    private static async Task<JournalEntry> ReadJournalEntryAsync(SqliteConnection connection, SqliteTransaction? transaction, Guid id, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = EntrySelect + " WHERE e.id=$id;";
        command.Parameters.AddWithValue("$id", Id(id));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadJournalEntry(reader) : throw new SnookException(SnookErrorCode.NotFound, "The journal entry does not exist.");
    }

    private static JournalEntry ReadJournalEntry(SqliteDataReader reader) => new(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
        reader.GetString(2), reader.GetString(3), FromMs(reader.GetInt64(4)), reader.IsDBNull(5) ? null : reader.GetInt32(5),
        JsonSerializer.Deserialize<string[]>(reader.GetString(10))!, FromMs(reader.GetInt64(6)), FromMs(reader.GetInt64(7)), NullableMs(reader, 8), reader.GetInt64(9));

    private async Task<object> ExportJournalsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var journals = await ReadJournalsAsync(connection, transaction, true, cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = EntrySelect + " ORDER BY e.occurred_at_utc_ms,e.id;";
        var entries = new List<JournalEntry>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken)) entries.Add(ReadJournalEntry(reader));
        }
        await transaction.CommitAsync(cancellationToken);
        return new { journals, entries };
    }

    private static void ValidateJournalIdRevision(Guid? id, long? revision)
    {
        if (id == Guid.Empty || (id is null ? revision is not null : revision is null or < 1))
            throw JournalInvalid("Provide a valid ID and expected revision for changes, and neither for creation.");
    }
    private static void CheckJournalRevision(long revision, long? expected)
    {
        if (revision != expected) throw new SnookException(SnookErrorCode.RevisionConflict,
            "This journal or entry changed in another view. Your draft is still here. Review the latest saved version before saving again.");
    }
    private static void EnsureJournalActive(Journal journal)
    {
        if (journal.DeletedAtUtc is not null) throw new SnookException(SnookErrorCode.InvalidTransition, "Restore the deleted journal first.");
    }
    private static object DbInstant(DateTimeOffset? value) => (object?)value?.ToUnixTimeMilliseconds() ?? DBNull.Value;
    private static SnookException JournalInvalid(string message) => new(SnookErrorCode.ValidationFailed, message);
    private sealed record JournalCursor(string Scope, long Time, Guid Id);
    private sealed record JournalReceipt<T>(string Fingerprint, StoreJournalResult<T> Result);
}
