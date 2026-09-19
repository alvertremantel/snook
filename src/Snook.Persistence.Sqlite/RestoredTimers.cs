using Microsoft.Data.Sqlite;
using Snook.Domain;

namespace Snook.Persistence.Sqlite;

public sealed partial class SqliteStore
{
    private static async Task ReconcileRestoredTimersAsync(SqliteConnection connection, DateTimeOffset backupAtUtc,
        DateTimeOffset restoredAtUtc, CancellationToken cancellationToken)
    {
        var candidates = new List<Guid>();
        await using (var query = connection.CreateCommand())
        {
            query.CommandText = """
                SELECT id FROM tracking_sessions s WHERE deleted_at_utc_ms IS NULL
                AND (state='running' OR EXISTS(SELECT 1 FROM active_intervals i WHERE i.session_id=s.id AND i.ended_at_utc_ms IS NULL))
                ORDER BY id;
                """;
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) candidates.Add(Guid.Parse(reader.GetString(0)));
        }
        if (candidates.Count == 0) return;
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        foreach (var id in candidates)
        {
            var before = await ReadSessionAsync(connection, transaction, id, cancellationToken)
                ?? throw InvalidBackup("A restored timer could not be read safely.");
            await using (var close = connection.CreateCommand())
            {
                close.Transaction = transaction;
                close.CommandText = "UPDATE active_intervals SET ended_at_utc_ms=MAX(started_at_utc_ms,$backup),revision=revision+1 WHERE session_id=$id AND ended_at_utc_ms IS NULL;";
                close.Parameters.AddWithValue("$backup", backupAtUtc.ToUnixTimeMilliseconds());
                close.Parameters.AddWithValue("$id", Id(id));
                await close.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE tracking_sessions SET state='recovery-required',stopped_at_utc_ms=NULL,
                    recovery_status='workspace-restored',recovery_reason=$reason,updated_at_utc_ms=$now,revision=revision+1 WHERE id=$id;
                    """;
                update.Parameters.AddWithValue("$id", Id(id));
                update.Parameters.AddWithValue("$now", restoredAtUtc.ToUnixTimeMilliseconds());
                update.Parameters.AddWithValue("$reason", "Restored timer: recorded time is frozen at the backup boundary.");
                await update.ExecuteNonQueryAsync(cancellationToken);
            }
            var after = await ReadSessionAsync(connection, transaction, id, cancellationToken)
                ?? throw InvalidBackup("A restored timer disappeared during recovery preparation.");
            var operation = Guid.NewGuid();
            await InsertTrackingCorrectionAsync(connection, transaction, new TrackingCorrection(Guid.NewGuid(), id, operation,
                "Workspace restore froze open intervals at the verified backup boundary; the gap needs an explicit decision.",
                before, after, restoredAtUtc), cancellationToken);
            await RecordMutationAsync(connection, transaction, operation, "session", id, "recovery-required", before.Revision, after.Revision, restoredAtUtc, cancellationToken);
            await RecordReceiptAsync(connection, transaction, operation, id, after.Revision, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private static DateTimeOffset RestoredLastKnown(TrackingSession session)
        => session.Intervals.Aggregate(session.StartedAtUtc, (last, interval) => Max(last, interval.EndedAtUtc ?? interval.StartedAtUtc));
}
