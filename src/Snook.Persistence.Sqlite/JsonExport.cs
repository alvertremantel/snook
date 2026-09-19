using Snook.Domain;

namespace Snook.Persistence.Sqlite;

public sealed partial class SqliteStore
{
    private const int JsonExportSchemaVersion = 5;

    private async Task<IReadOnlyList<ExportTag>> ReadExportTagsAsync(CancellationToken cancellationToken)
    {
        await using var connectionLease = await OpenConnectionAsync(cancellationToken);
        var connection = connectionLease.Connection;
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,workspace_id,normalized_name,display_name,color,revision,deleted_at_utc_ms FROM tags ORDER BY normalized_name,id;";
        var tags = new List<ExportTag>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            tags.Add(new ExportTag(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetString(2),
                reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetInt64(5), NullableMs(reader, 6)));
        return tags;
    }

    private async Task<IReadOnlyList<ExportTrackingSession>> ReadExportSessionsAsync(CancellationToken cancellationToken)
    {
        await using var connectionLease = await OpenConnectionAsync(cancellationToken);
        var connection = connectionLease.Connection;
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,workspace_id,task_id,activity_id,lane,state,started_at_utc_ms,stopped_at_utc_ms,notes,created_at_utc_ms,updated_at_utc_ms,revision,recovery_status,recovery_reason,deleted_at_utc_ms FROM tracking_sessions ORDER BY started_at_utc_ms DESC,id;";
        var sessions = new List<ExportTrackingSession>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = Guid.Parse(reader.GetString(0));
            sessions.Add(new ExportTrackingSession(id, Guid.Parse(reader.GetString(1)),
                reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)), reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3)),
                reader.GetString(4) == "background" ? SessionLane.Background : SessionLane.Foreground, ParseState(reader.GetString(5)),
                FromMs(reader.GetInt64(6)), NullableMs(reader, 7), reader.GetString(8), FromMs(reader.GetInt64(9)),
                FromMs(reader.GetInt64(10)), reader.GetInt64(11), await ReadIntervalsAsync(connection, id, cancellationToken),
                reader.IsDBNull(12) ? null : reader.GetString(12), reader.IsDBNull(13) ? null : reader.GetString(13), NullableMs(reader, 14)));
        }
        return sessions;
    }

    // Export is a persisted snapshot, not the active-task details projection.
    // Do not filter by parent visibility or discard stored relationship metadata.
    private async Task<IReadOnlyList<ExportTaskLink>> ReadExportTaskLinksAsync(CancellationToken cancellationToken)
    {
        await using var connectionLease = await OpenConnectionAsync(cancellationToken);
        var connection = connectionLease.Connection;
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,task_id,label,uri,kind,created_at_utc_ms,deleted_at_utc_ms,revision FROM task_links ORDER BY task_id,created_at_utc_ms,id;";
        var links = new List<ExportTaskLink>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            links.Add(new ExportTaskLink(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
                reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3), reader.GetString(4),
                FromMs(reader.GetInt64(5)), NullableMs(reader, 6), reader.GetInt64(7)));
        return links;
    }

    private async Task<IReadOnlyList<ExportTaskDependency>> ReadExportTaskDependenciesAsync(CancellationToken cancellationToken)
    {
        await using var connectionLease = await OpenConnectionAsync(cancellationToken);
        var connection = connectionLease.Connection;
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,task_id,prerequisite_task_id,created_at_utc_ms,revision FROM task_dependencies ORDER BY task_id,created_at_utc_ms,id;";
        var dependencies = new List<ExportTaskDependency>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            dependencies.Add(new ExportTaskDependency(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
                Guid.Parse(reader.GetString(2)), FromMs(reader.GetInt64(3)), reader.GetInt64(4)));
        return dependencies;
    }

    private sealed record ExportTaskLink(Guid Id, Guid TaskId, string? Label, string Uri, string Kind,
        DateTimeOffset CreatedAtUtc, DateTimeOffset? DeletedAtUtc, long Revision);

    private sealed record ExportTaskDependency(Guid Id, Guid TaskId, Guid PrerequisiteTaskId,
        DateTimeOffset CreatedAtUtc, long Revision);

    private sealed record ExportTag(Guid Id, Guid WorkspaceId, string NormalizedName, string DisplayName,
        string? Color, long Revision, DateTimeOffset? DeletedAtUtc);

    private sealed record ExportTrackingSession(Guid Id, Guid WorkspaceId, Guid? TaskId, Guid? ActivityId,
        SessionLane Lane, SessionState State, DateTimeOffset StartedAtUtc, DateTimeOffset? StoppedAtUtc, string Notes,
        DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, long Revision, IReadOnlyList<TimeInterval> Intervals,
        string? RecoveryStatus, string? RecoveryReason, DateTimeOffset? DeletedAtUtc);
}
