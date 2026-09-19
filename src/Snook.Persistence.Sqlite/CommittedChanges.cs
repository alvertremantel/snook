using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

namespace Snook.Persistence.Sqlite;

public sealed record StoreCommittedChange(long Cursor, Guid OperationId, string AggregateType,
    Guid AggregateId, string Kind, long NewRevision, DateTimeOffset CommittedAtUtc);

public sealed partial class SqliteStore
{
    private readonly ConcurrentQueue<StoreCommittedChange> _committedChanges = new();
    private int _deliveringChanges;

    /// <summary>
    /// Fresh committed mutations and restore invalidations in commit order. Handlers must not block; they may
    /// enqueue work or reenter the store. Handler failure cannot undo a commit.
    /// </summary>
    public event EventHandler<StoreCommittedChange>? Committed;

    private static async Task<IReadOnlyList<StoreCommittedChange>> ReadTransactionChangesAsync(
        SqliteConnection connection, SqliteTransaction transaction, long previousCursor, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT sequence, operation_id, aggregate_type, aggregate_id, kind, new_revision, occurred_at_utc_ms
            FROM mutation_log WHERE sequence > $previous ORDER BY sequence;
            """;
        command.Parameters.AddWithValue("$previous", previousCursor);
        var changes = new List<StoreCommittedChange>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            changes.Add(new StoreCommittedChange(reader.GetInt64(0), Guid.Parse(reader.GetString(1)),
                reader.GetString(2), Guid.Parse(reader.GetString(3)), reader.GetString(4), reader.GetInt64(5),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6))));
        return changes;
    }

    private void DeliverCommittedChanges()
    {
        // Enqueue while holding the write gate, but invoke outside it so a handler
        // cannot deadlock a nested operation. A single drainer preserves ordering.
        do
        {
            if (Interlocked.CompareExchange(ref _deliveringChanges, 1, 0) != 0) return;
            try
            {
                while (_committedChanges.TryDequeue(out var change))
                {
                    if (Committed is not { } handlers) continue;
                    foreach (EventHandler<StoreCommittedChange> handler in handlers.GetInvocationList())
                    {
                        try { handler(this, change); }
                        catch (Exception) { /* Observers cannot turn a durable commit into a failed mutation. */ }
                    }
                }
            }
            finally { Volatile.Write(ref _deliveringChanges, 0); }
        } while (!_committedChanges.IsEmpty);
    }
}
