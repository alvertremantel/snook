using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Snook.Domain;

namespace Snook.Persistence.Sqlite;

public sealed partial class SqliteStore
{
    private const string ExactReceiptsSql = """
        CREATE TABLE exact_operation_receipts(
            operation_id TEXT PRIMARY KEY REFERENCES operation_receipts(operation_id),
            format INTEGER NOT NULL CHECK(format=1),
            request_hash TEXT NOT NULL,
            result_json TEXT NOT NULL);
        """;
    private static readonly string ExactReceiptsChecksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ExactReceiptsSql))).ToLowerInvariant();

    // Method keys and argument names are durable receipt identities; do not rename
    // them as incidental implementation cleanup. Host clock/policy is not caller input.
    private sealed record StoreWriteRequest(Guid OperationId, string Method, object Arguments)
    {
        public string Fingerprint { get; } = Convert.ToHexString(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(new { method = Method, arguments = Arguments })));
    }

    private static async Task EnsureExactReceiptsMigrationAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.CommandText = "SELECT MAX(sequence) FROM schema_migrations;";
        if (Convert.ToInt32(await query.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) >= 10) return;
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteMigrationCommandAsync(connection, transaction, ExactReceiptsSql, cancellationToken);
        await ExecuteMigrationCommandAsync(connection, transaction,
            "INSERT INTO schema_migrations(sequence,name,checksum,applied_at_utc_ms) VALUES(10,'exact-operation-receipts',$checksum,$applied);",
            cancellationToken, ("$checksum", ExactReceiptsChecksum), ("$applied", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<(bool Found, T? Value)> ReadExactReceiptAsync<T>(SqliteConnection connection,
        SqliteTransaction transaction, StoreWriteRequest request, CancellationToken cancellationToken)
    {
        if (request.OperationId == Guid.Empty)
            throw new SnookException(SnookErrorCode.ValidationFailed, "An operation ID is required.");
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT format,request_hash,result_json FROM exact_operation_receipts WHERE operation_id=$id;";
        command.Parameters.AddWithValue("$id", Id(request.OperationId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return (false, default);
        if (reader.GetInt32(0) != 1)
            throw new SnookException(SnookErrorCode.SchemaIncompatible, "The saved operation receipt format is unsupported.");
        if (!string.Equals(reader.GetString(1), request.Fingerprint, StringComparison.Ordinal))
            throw new SnookException(SnookErrorCode.ValidationFailed, "This operation ID was already used for a different request.");
        try
        {
            return (true, JsonSerializer.Deserialize<T>(reader.GetString(2))
                ?? throw new JsonException("Missing receipt result."));
        }
        catch (JsonException exception)
        {
            throw new SnookException(SnookErrorCode.SchemaIncompatible, "The saved operation receipt is invalid.", exception);
        }
    }

    private static async Task SaveExactReceiptAsync<T>(SqliteConnection connection, SqliteTransaction transaction,
        StoreWriteRequest request, T result, CancellationToken cancellationToken)
    {
        // No-op commands also retain their exact result. Reserve their operation ID
        // in the shared receipt catalog so other mutation families cannot reuse it.
        await ExecuteMigrationCommandAsync(connection, transaction,
            "INSERT INTO operation_receipts(operation_id,status,aggregate_id,aggregate_revision) VALUES($id,'applied',$aggregate,0) ON CONFLICT(operation_id) DO NOTHING;",
            cancellationToken, ("$id", Id(request.OperationId)), ("$aggregate", Id(Guid.Empty)));
        await ExecuteMigrationCommandAsync(connection, transaction,
            "INSERT INTO exact_operation_receipts(operation_id,format,request_hash,result_json) VALUES($id,1,$hash,$result);",
            cancellationToken, ("$id", Id(request.OperationId)), ("$hash", request.Fingerprint), ("$result", JsonSerializer.Serialize(result)));
    }

    private static async Task ValidateAdditiveMigrationsAsync(SqliteConnection connection, int latest, CancellationToken cancellationToken)
    {
        string[] checksums = [TaskWorkspaceChecksum, HabitsChecksum, JournalsChecksum, ExactReceiptsChecksum];
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sequence,checksum FROM schema_migrations WHERE sequence>=7 ORDER BY sequence;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        for (var sequence = 7; sequence <= latest; sequence++)
        {
            if (!await reader.ReadAsync(cancellationToken) || reader.GetInt32(0) != sequence || reader.GetString(1) != checksums[sequence - 7])
                throw new SnookException(SnookErrorCode.SchemaIncompatible, "The workspace migration history is incompatible with this Snook build.");
        }
    }
}
