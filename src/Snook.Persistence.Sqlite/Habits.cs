using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Snook.Domain;

namespace Snook.Persistence.Sqlite;

public sealed record StoreHabitResult(Habit Habit, long? Cursor, string? Kind, DateTimeOffset CommittedAtUtc);

public sealed partial class SqliteStore
{
    private const string HabitsSql = """
        CREATE TABLE habits(
            id TEXT PRIMARY KEY, name TEXT NOT NULL, description TEXT NOT NULL,
            start_date TEXT NOT NULL, time_zone TEXT NOT NULL, created_at_utc_ms INTEGER NOT NULL,
            archived_at_utc_ms INTEGER, deleted_at_utc_ms INTEGER, revision INTEGER NOT NULL CHECK(revision>0));
        CREATE TABLE habit_check_ins(
            habit_id TEXT NOT NULL REFERENCES habits(id), day TEXT NOT NULL, completed_at_utc_ms INTEGER NOT NULL,
            PRIMARY KEY(habit_id,day));
        """;
    private static readonly string HabitsChecksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(HabitsSql))).ToLowerInvariant();

    private static async Task EnsureHabitsMigrationAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.CommandText = "SELECT MAX(sequence) FROM schema_migrations;";
        if (Convert.ToInt32(await query.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) >= 8) return;
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteMigrationCommandAsync(connection, transaction, HabitsSql, cancellationToken);
        await ExecuteMigrationCommandAsync(connection, transaction,
            "INSERT INTO schema_migrations(sequence,name,checksum,applied_at_utc_ms) VALUES(8,'daily-habits',$checksum,$applied);",
            cancellationToken, ("$checksum", HabitsChecksum), ("$applied", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<HabitProgress>> GetHabitsAsync(int days, DateOnly? throughDate, bool includeArchived,
        bool includeDeleted, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        if (days is < 1 or > HabitRules.MaximumDays || throughDate is { Year: < 1900 })
            throw new SnookException(SnookErrorCode.ValidationFailed, "Read 1–366 days, with dates on or after 1900-01-01.");
        await using var connectionLease = await OpenConnectionAsync(cancellationToken);
        var connection = connectionLease.Connection;
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var habits = await ReadHabitsAsync(connection, transaction, cancellationToken);
        var result = new List<HabitProgress>();
        foreach (var habit in habits.Where(habit => habit.DeletedAtUtc is not null
                     ? includeDeleted : includeArchived || habit.ArchivedAtUtc is null))
        {
            var today = HabitRules.Today(nowUtc, habit.TimeZone);
            var end = throughDate ?? today;
            var start = end.AddDays(1 - days);
            var checkIns = await ReadHabitCheckInsAsync(connection, transaction, habit.Id, start, end, cancellationToken);
            // A pending today leaves yesterday's streak intact. Indexed recursion stops
            // at the first missing civil date; no history is sent to calculate a streak.
            await using var streak = connection.CreateCommand();
            streak.Transaction = transaction;
            streak.CommandText = """
                WITH RECURSIVE chain(day) AS (
                    SELECT CASE WHEN EXISTS(SELECT 1 FROM habit_check_ins WHERE habit_id=$id AND day=$today)
                        THEN $today ELSE date($today,'-1 day') END
                    UNION ALL SELECT date(day,'-1 day') FROM chain
                        WHERE day >= $start AND EXISTS(SELECT 1 FROM habit_check_ins WHERE habit_id=$id AND habit_check_ins.day=chain.day)
                ) SELECT COUNT(*) FROM chain WHERE EXISTS(SELECT 1 FROM habit_check_ins WHERE habit_id=$id AND habit_check_ins.day=chain.day);
                """;
            streak.Parameters.AddWithValue("$id", Id(habit.Id));
            streak.Parameters.AddWithValue("$today", HabitDate(today));
            streak.Parameters.AddWithValue("$start", HabitDate(habit.StartDate));
            var currentStreak = Convert.ToInt32(await streak.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
            var eligible = Math.Max(0, Math.Min(end.DayNumber, today.DayNumber) - Math.Max(start.DayNumber, habit.StartDate.DayNumber) + 1);
            result.Add(new HabitProgress(habit, today, start, end, checkIns, currentStreak, eligible));
        }
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public Task<StoreHabitResult> CreateHabitAsync(HabitDefinition definition, Guid operationId, DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        if (definition is null) throw new SnookException(SnookErrorCode.ValidationFailed, "A habit definition is required.");
        var (name, description) = HabitRules.ValidateText(definition.Name, definition.Description);
        var zone = HabitRules.ResolveZone(definition.TimeZone).Id;
        var fingerprint = JsonSerializer.Serialize(new { Kind = "create-habit", Definition = definition });
        return WriteHabitAsync(operationId, fingerprint, async (connection, transaction) =>
        {
            var today = HabitRules.Today(nowUtc, zone);
            HabitRules.ValidateDate(definition.StartDate, today.AddDays(1 - HabitRules.MaximumDays), today);
            await using var count = connection.CreateCommand();
            count.Transaction = transaction;
            count.CommandText = "SELECT COUNT(*) FROM habits;";
            if (Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) >= HabitRules.MaximumHabits)
                throw new SnookException(SnookErrorCode.ValidationFailed, "This workspace has reached the limit of 500 habits, including deleted habits.");
            var habit = new Habit(Guid.NewGuid(), name, description, definition.StartDate, zone, MillisecondUtc(nowUtc), null, null, 1);
            await ExecuteMigrationCommandAsync(connection, transaction,
                "INSERT INTO habits(id,name,description,start_date,time_zone,created_at_utc_ms,revision) VALUES($id,$name,$description,$start,$zone,$now,1);",
                cancellationToken, ("$id", Id(habit.Id)), ("$name", name), ("$description", description),
                ("$start", HabitDate(habit.StartDate)), ("$zone", zone), ("$now", nowUtc.ToUnixTimeMilliseconds()));
            return (habit, "created");
        }, nowUtc, cancellationToken);
    }

    public Task<StoreHabitResult> UpdateHabitAsync(Guid habitId, HabitUpdate update, long expectedRevision, Guid operationId,
        DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        if (update is null) throw new SnookException(SnookErrorCode.ValidationFailed, "A habit update is required.");
        var (name, description) = HabitRules.ValidateText(update.Name, update.Description);
        return ChangeHabitAsync(habitId, expectedRevision, operationId, "updated", update, (habit, _, _) =>
        {
            EnsureHabitActive(habit);
            return Task.FromResult(habit with { Name = name, Description = description });
        }, nowUtc, cancellationToken);
    }

    public Task<StoreHabitResult> SetHabitCompletionAsync(Guid habitId, DateOnly date, bool completed, long expectedRevision,
        Guid operationId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
        => ChangeHabitAsync(habitId, expectedRevision, operationId, "check-in", new { date, completed }, async (habit, connection, transaction) =>
        {
            EnsureHabitActive(habit);
            HabitRules.ValidateDate(date, habit.StartDate, HabitRules.Today(nowUtc, habit.TimeZone));
            await ExecuteMigrationCommandAsync(connection, transaction, completed
                ? "INSERT OR IGNORE INTO habit_check_ins(habit_id,day,completed_at_utc_ms) VALUES($id,$day,$now);"
                : "DELETE FROM habit_check_ins WHERE habit_id=$id AND day=$day;", cancellationToken,
                ("$id", Id(habitId)), ("$day", HabitDate(date)), ("$now", nowUtc.ToUnixTimeMilliseconds()));
            return habit;
        }, nowUtc, cancellationToken);

    public Task<StoreHabitResult> SetHabitArchivedAsync(Guid habitId, bool archived, long expectedRevision, Guid operationId,
        DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
        => ChangeHabitAsync(habitId, expectedRevision, operationId, archived ? "archived" : "unarchived", archived, (habit, _, _) =>
        {
            if (habit.DeletedAtUtc is not null) throw new SnookException(SnookErrorCode.InvalidTransition, "Restore the deleted habit first.");
            return Task.FromResult(habit with { ArchivedAtUtc = archived ? habit.ArchivedAtUtc ?? MillisecondUtc(nowUtc) : null });
        }, nowUtc, cancellationToken);

    public Task<StoreHabitResult> SetHabitDeletedAsync(Guid habitId, bool deleted, long expectedRevision, Guid operationId,
        DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
        => ChangeHabitAsync(habitId, expectedRevision, operationId, deleted ? "deleted" : "restored-deleted", deleted,
            (habit, _, _) => Task.FromResult(habit with { DeletedAtUtc = deleted ? habit.DeletedAtUtc ?? MillisecondUtc(nowUtc) : null }), nowUtc, cancellationToken);

    private Task<StoreHabitResult> ChangeHabitAsync(Guid habitId, long expectedRevision, Guid operationId, string kind, object arguments,
        Func<Habit, SqliteConnection, SqliteTransaction, Task<Habit>> change, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        if (habitId == Guid.Empty || expectedRevision < 1)
            throw new SnookException(SnookErrorCode.ValidationFailed, "A habit ID and positive expected revision are required.");
        var fingerprint = JsonSerializer.Serialize(new { Kind = kind, HabitId = habitId, ExpectedRevision = expectedRevision, Arguments = arguments });
        return WriteHabitAsync(operationId, fingerprint, async (connection, transaction) =>
        {
            var habit = (await ReadHabitsAsync(connection, transaction, cancellationToken)).SingleOrDefault(item => item.Id == habitId)
                ?? throw new SnookException(SnookErrorCode.NotFound, "The habit does not exist.");
            if (habit.Revision != expectedRevision)
                throw new SnookException(SnookErrorCode.RevisionConflict, "This habit changed in another view. Your draft is still here. Reload the saved habit and review your edits before saving again.");
            var updated = (await change(habit, connection, transaction)) with { Revision = habit.Revision + 1 };
            await ExecuteMigrationCommandAsync(connection, transaction,
                "UPDATE habits SET name=$name,description=$description,archived_at_utc_ms=$archived,deleted_at_utc_ms=$deleted,revision=$revision WHERE id=$id;",
                cancellationToken, ("$name", updated.Name), ("$description", updated.Description), ("$archived", (object?)updated.ArchivedAtUtc?.ToUnixTimeMilliseconds() ?? DBNull.Value),
                ("$deleted", (object?)updated.DeletedAtUtc?.ToUnixTimeMilliseconds() ?? DBNull.Value), ("$revision", updated.Revision), ("$id", Id(habitId)));
            return (updated, kind);
        }, nowUtc, cancellationToken);
    }

    private Task<StoreHabitResult> WriteHabitAsync(Guid operationId, string fingerprint,
        Func<SqliteConnection, SqliteTransaction, Task<(Habit Habit, string Kind)>> change,
        DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        EnsureInitialized();
        if (operationId == Guid.Empty) throw new SnookException(SnookErrorCode.ValidationFailed, "An operation ID is required.");
        return WriteAsync(async (connection, transaction) =>
        {
            await using var receipt = connection.CreateCommand();
            receipt.Transaction = transaction;
            receipt.CommandText = "SELECT result_payload FROM operation_receipts WHERE operation_id=$id;";
            receipt.Parameters.AddWithValue("$id", Id(operationId));
            var payload = await receipt.ExecuteScalarAsync(cancellationToken);
            if (payload is not null)
            {
                HabitReceipt? saved = null;
                if (payload is string json)
                {
                    try { saved = JsonSerializer.Deserialize<HabitReceipt>(json); }
                    catch (JsonException) { /* An operation of another type owns this receipt. */ }
                }
                if (saved?.Fingerprint != fingerprint || saved.Habit is null)
                    throw new SnookException(SnookErrorCode.ValidationFailed, "This operation ID was already used for a different request.");
                return new StoreHabitResult(saved.Habit, null, null, default);
            }
            var (habit, kind) = await change(connection, transaction);
            await RecordMutationAsync(connection, transaction, operationId, "habit", habit.Id, kind, habit.Revision - 1, habit.Revision, nowUtc, cancellationToken);
            await using var cursorQuery = connection.CreateCommand();
            cursorQuery.Transaction = transaction;
            cursorQuery.CommandText = "SELECT last_insert_rowid();";
            var cursor = Convert.ToInt64(await cursorQuery.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
            await RecordReceiptAsync(connection, transaction, operationId, habit.Id, habit.Revision, cancellationToken);
            await ExecuteMigrationCommandAsync(connection, transaction,
                "UPDATE operation_receipts SET result_payload=$payload WHERE operation_id=$id;", cancellationToken,
                ("$payload", JsonSerializer.Serialize(new HabitReceipt(fingerprint, habit))), ("$id", Id(operationId)));
            return new StoreHabitResult(habit, cursor, kind, MillisecondUtc(nowUtc));
        }, cancellationToken);
    }

    private static async Task<List<Habit>> ReadHabitsAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id,name,description,start_date,time_zone,created_at_utc_ms,archived_at_utc_ms,deleted_at_utc_ms,revision FROM habits ORDER BY created_at_utc_ms,id LIMIT 501;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<Habit>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new Habit(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), DateOnly.ParseExact(reader.GetString(3), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                reader.GetString(4), FromMs(reader.GetInt64(5)), NullableMs(reader, 6), NullableMs(reader, 7), reader.GetInt64(8)));
        if (result.Count > HabitRules.MaximumHabits) throw new SnookException(SnookErrorCode.ValidationFailed, "The habit catalog exceeds the supported limit of 500.");
        return result;
    }

    private static async Task<List<HabitCheckIn>> ReadHabitCheckInsAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid habitId, DateOnly start, DateOnly end, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT day,completed_at_utc_ms FROM habit_check_ins WHERE habit_id=$id AND day BETWEEN $start AND $end ORDER BY day;";
        command.Parameters.AddWithValue("$id", Id(habitId));
        command.Parameters.AddWithValue("$start", HabitDate(start));
        command.Parameters.AddWithValue("$end", HabitDate(end));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<HabitCheckIn>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new HabitCheckIn(habitId, DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture), FromMs(reader.GetInt64(1))));
        return result;
    }

    private async Task<object> ExportHabitsAsync(CancellationToken cancellationToken)
    {
        await using var connectionLease = await OpenConnectionAsync(cancellationToken);
        var connection = connectionLease.Connection;
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var habits = await ReadHabitsAsync(connection, transaction, cancellationToken);
        var checkIns = new List<HabitCheckIn>();
        foreach (var habit in habits)
            checkIns.AddRange(await ReadHabitCheckInsAsync(connection, transaction, habit.Id, DateOnly.MinValue, DateOnly.MaxValue, cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return new { habits, checkIns };
    }

    private static void EnsureHabitActive(Habit habit)
    {
        if (habit.ArchivedAtUtc is not null || habit.DeletedAtUtc is not null)
            throw new SnookException(SnookErrorCode.InvalidTransition, "Restore the habit before editing or checking off days.");
    }

    private static string HabitDate(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static DateTimeOffset MillisecondUtc(DateTimeOffset instant) => DateTimeOffset.FromUnixTimeMilliseconds(instant.ToUnixTimeMilliseconds());
    private sealed record HabitReceipt(string Fingerprint, Habit Habit);
}
