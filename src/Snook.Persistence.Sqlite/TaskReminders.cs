using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Snook.Domain;

namespace Snook.Persistence.Sqlite;

public sealed partial class SqliteStore
{
    // Sequence 11 repairs existing sequence-10 workspaces. Timed-deadline columns
    // are present in the current fresh schema, but were not part of any historical
    // additive migration and therefore must never be assumed to exist on upgrade.
    private const string TaskRemindersSql = """
        tasks.due_at_utc_ms INTEGER;
        tasks.due_time_zone TEXT;
        tasks.reminder_id TEXT;
        tasks.reminder_local_date TEXT;
        tasks.reminder_local_time TEXT;
        tasks.reminder_fold TEXT;
        tasks.reminder_delivery TEXT;
        CREATE INDEX IF NOT EXISTS tasks_open_due_at ON tasks(due_at_utc_ms,project_id)
            WHERE due_at_utc_ms IS NOT NULL AND status='open' AND archived_at_utc_ms IS NULL AND deleted_at_utc_ms IS NULL;
        """;
    private static readonly string TaskRemindersChecksum = Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(TaskRemindersSql))).ToLowerInvariant();

    private static async Task EnsureTaskRemindersMigrationAsync(SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.CommandText = "SELECT MAX(sequence) FROM schema_migrations;";
        if (Convert.ToInt32(await query.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) >= 11) return;
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            if (!await HasColumnAsync(connection, "tasks", "due_at_utc_ms", cancellationToken))
                await ExecuteMigrationCommandAsync(connection, transaction,
                    "ALTER TABLE tasks ADD COLUMN due_at_utc_ms INTEGER;", cancellationToken);
            if (!await HasColumnAsync(connection, "tasks", "due_time_zone", cancellationToken))
                await ExecuteMigrationCommandAsync(connection, transaction,
                    "ALTER TABLE tasks ADD COLUMN due_time_zone TEXT;", cancellationToken);
            if (!await HasColumnAsync(connection, "tasks", "reminder_id", cancellationToken))
                await ExecuteMigrationCommandAsync(connection, transaction,
                    "ALTER TABLE tasks ADD COLUMN reminder_id TEXT;", cancellationToken);
            if (!await HasColumnAsync(connection, "tasks", "reminder_local_date", cancellationToken))
                await ExecuteMigrationCommandAsync(connection, transaction,
                    "ALTER TABLE tasks ADD COLUMN reminder_local_date TEXT;", cancellationToken);
            if (!await HasColumnAsync(connection, "tasks", "reminder_local_time", cancellationToken))
                await ExecuteMigrationCommandAsync(connection, transaction,
                    "ALTER TABLE tasks ADD COLUMN reminder_local_time TEXT;", cancellationToken);
            if (!await HasColumnAsync(connection, "tasks", "reminder_fold", cancellationToken))
                await ExecuteMigrationCommandAsync(connection, transaction,
                    "ALTER TABLE tasks ADD COLUMN reminder_fold TEXT;", cancellationToken);
            if (!await HasColumnAsync(connection, "tasks", "reminder_delivery", cancellationToken))
                await ExecuteMigrationCommandAsync(connection, transaction,
                    "ALTER TABLE tasks ADD COLUMN reminder_delivery TEXT;", cancellationToken);
            await BackfillLegacyRemindersAsync(connection, transaction, cancellationToken);
            await ExecuteMigrationCommandAsync(connection, transaction,
                "CREATE INDEX IF NOT EXISTS tasks_open_due_at ON tasks(due_at_utc_ms,project_id) WHERE due_at_utc_ms IS NOT NULL AND status='open' AND archived_at_utc_ms IS NULL AND deleted_at_utc_ms IS NULL;",
                cancellationToken);
            await ExecuteMigrationCommandAsync(connection, transaction,
                "INSERT INTO schema_migrations(sequence,name,checksum,applied_at_utc_ms) VALUES(11,'task-reminders',$checksum,$applied);",
                cancellationToken, ("$checksum", TaskRemindersChecksum),
                ("$applied", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task BackfillLegacyRemindersAsync(SqliteConnection connection,
        SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        var rows = new List<(Guid TaskId, DateTimeOffset DueAtUtc, string Zone)>();
        await using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = "SELECT id,due_at_utc_ms,due_time_zone FROM tasks WHERE due_at_utc_ms IS NOT NULL AND due_time_zone IS NOT NULL AND reminder_id IS NULL;";
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                rows.Add((Guid.Parse(reader.GetString(0)),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)), reader.GetString(2)));
        }
        foreach (var row in rows)
        {
            TimeZoneInfo zone;
            try { zone = TimeZoneInfo.FindSystemTimeZoneById(row.Zone); }
            catch (Exception exception) when (exception is TimeZoneNotFoundException
                or InvalidTimeZoneException or ArgumentException)
            {
                throw new SnookException(SnookErrorCode.SchemaIncompatible,
                    "A legacy timed deadline contains an invalid time zone.", exception);
            }
            var local = TimeZoneInfo.ConvertTime(row.DueAtUtc, zone).DateTime;
            var fold = AmbiguousTimeChoice.Earlier;
            var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
            if (zone.IsAmbiguousTime(unspecified))
            {
                var candidates = zone.GetAmbiguousTimeOffsets(unspecified)
                    .Select(offset => new DateTimeOffset(unspecified, offset).ToUniversalTime())
                    .OrderBy(value => value).ToArray();
                if (row.DueAtUtc == candidates[^1]) fold = AmbiguousTimeChoice.Later;
            }
            await ExecuteMigrationCommandAsync(connection, transaction, """
                UPDATE tasks SET reminder_id=$reminder,reminder_local_date=$date,
                    reminder_local_time=$time,reminder_fold=$fold,reminder_delivery='inexact'
                WHERE id=$id AND reminder_id IS NULL;
                """, cancellationToken,
                ("$reminder", Id(Guid.NewGuid())),
                ("$date", DateOnly.FromDateTime(local).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                ("$time", TimeOnly.FromDateTime(local).ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture)),
                ("$fold", fold == AmbiguousTimeChoice.Earlier ? "earlier" : "later"),
                ("$id", Id(row.TaskId)));
        }
    }
}
