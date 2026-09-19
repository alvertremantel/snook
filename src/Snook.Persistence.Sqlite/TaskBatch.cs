using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Snook.Domain;

namespace Snook.Persistence.Sqlite;

public sealed partial class SqliteStore
{
    // Date-only due dates and favorite columns already exist in the original schema.
    // Keep this migration separate from the historical schema checksum so existing stores upgrade.
    private const string TaskWorkspaceSql = """
        CREATE INDEX IF NOT EXISTS tasks_open_due_date ON tasks(due_date,project_id)
            WHERE due_date IS NOT NULL AND status='open' AND archived_at_utc_ms IS NULL AND deleted_at_utc_ms IS NULL;
        CREATE INDEX IF NOT EXISTS tasks_starred ON tasks(project_id) WHERE starred=1;
        CREATE INDEX IF NOT EXISTS projects_starred ON projects(board_id) WHERE starred=1;
        """;
    private static readonly string TaskWorkspaceChecksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(TaskWorkspaceSql))).ToLowerInvariant();

    private static async Task EnsureTaskWorkspaceMigrationAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.CommandText = "SELECT MAX(sequence) FROM schema_migrations;";
        if (Convert.ToInt32(await query.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) >= 7) return;
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteMigrationCommandAsync(connection, transaction, TaskWorkspaceSql, cancellationToken);
        await ExecuteMigrationCommandAsync(connection, transaction,
            "INSERT INTO schema_migrations(sequence,name,checksum,applied_at_utc_ms) VALUES(7,'task-due-dates-and-favorites',$checksum,$applied);",
            cancellationToken, ("$checksum", TaskWorkspaceChecksum), ("$applied", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TaskItem>> BulkUpdateTasksAsync(IReadOnlyList<TaskRevision> tasks, BulkTaskUpdate update,
        Guid operationId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        nowUtc = DateTimeOffset.FromUnixTimeMilliseconds(nowUtc.ToUnixTimeMilliseconds());
        if (tasks is null || update is null || operationId == Guid.Empty || tasks.Count is < 1 or > 500
            || tasks.Any(task => task is null || task.TaskId == Guid.Empty || task.ExpectedRevision < 1)
            || tasks.Select(task => task.TaskId).Distinct().Count() != tasks.Count)
            throw new SnookException(SnookErrorCode.ValidationFailed, "Choose 1–500 distinct tasks with valid revisions and an operation ID.");
        if ((update.Priority is { } priority && !Enum.IsDefined(priority)) || (update.Status is { } state && !Enum.IsDefined(state)))
            throw new SnookException(SnookErrorCode.ValidationFailed, "The task priority or status is invalid.");
        if (update.ChangeDueDate && update.DueDate is { Year: < 1900 })
            throw new SnookException(SnookErrorCode.ValidationFailed, "Due dates must be on or after 1900-01-01.");
        var title = update.Title is null ? null : Guard.Required(update.Title, "title", 300);
        var description = update.Description is null ? null : Guard.Optional(update.Description, "description", 20_000);
        var addTags = ValidateBatchTags(update.TagsToAdd);
        var removeTags = ValidateBatchTags(update.TagsToRemove);
        if (addTags.Intersect(removeTags, StringComparer.OrdinalIgnoreCase).Any())
            throw new SnookException(SnookErrorCode.ValidationFailed, "A tag cannot be both added and removed.");
        if (title is null && description is null && update.Priority is null && update.Status is null && !update.ChangeDueDate
            && !update.ChangeActivity && update.Starred is null && update.ProjectId is null && update.Archived is null
            && addTags.Length == 0 && removeTags.Length == 0)
            throw new SnookException(SnookErrorCode.ValidationFailed, "Choose at least one field to change.");

        return await WriteAsync<IReadOnlyList<TaskItem>>(async (connection, transaction) =>
        {
            if (update.ProjectId is { } projectId) await EnsureBatchProjectAsync(connection, transaction, projectId, cancellationToken);
            if (update.ChangeActivity && update.DefaultActivityId is { } activityId)
                await EnsureActivityAsync(connection, transaction, activityId, cancellationToken);

            var result = new List<TaskItem>(tasks.Count);
            foreach (var target in tasks)
            {
                var before = await ReadTaskAsync(connection, transaction, target.TaskId, cancellationToken)
                    ?? throw new SnookException(SnookErrorCode.NotFound, "A selected task no longer exists. No tasks were changed.");
                if (before.Revision != target.ExpectedRevision)
                    throw new SnookException(SnookErrorCode.RevisionConflict, $"“{before.Title}” changed in another view. No tasks were changed. Review the selection and retry.");
                if (before.ArchivedAtUtc is not null && update.Archived != false)
                    throw new SnookException(SnookErrorCode.InvalidTransition, "Restore archived tasks before editing. No tasks were changed.");
                await EnsureBatchProjectAsync(connection, transaction, before.ProjectId, cancellationToken);
                var after = before with
                {
                    Title = title ?? before.Title,
                    Description = description ?? before.Description,
                    Priority = update.Priority ?? before.Priority,
                    Status = update.Status ?? before.Status,
                    CompletedAtUtc = update.Status is null ? before.CompletedAtUtc
                        : update.Status == TaskState.Completed ? before.CompletedAtUtc ?? nowUtc : null,
                    DueDate = update.ChangeDueDate ? update.DueDate : before.DueDate,
                    DueAtUtc = update.ChangeDueDate ? null : before.DueAtUtc,
                    DueTimeZone = update.ChangeDueDate ? null : before.DueTimeZone,
                    DefaultActivityId = update.ChangeActivity ? update.DefaultActivityId : before.DefaultActivityId,
                    Starred = update.Starred ?? before.Starred,
                    ProjectId = update.ProjectId ?? before.ProjectId,
                    ArchivedAtUtc = update.Archived is null ? before.ArchivedAtUtc : update.Archived.Value ? before.ArchivedAtUtc ?? nowUtc : null,
                    Revision = before.Revision + 1
                };
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE tasks SET title=$title,description=$description,priority=$priority,status=$status,
                        completed_at_utc_ms=$completed,due_date=$due_date,due_at_utc_ms=$due_at,due_time_zone=$due_zone,
                        default_activity_id=$activity,starred=$starred,project_id=$project,archived_at_utc_ms=$archived,
                        revision=$revision,updated_at_utc_ms=$updated WHERE id=$id AND revision=$expected;
                    """;
                BindTask(command, after, nowUtc);
                command.Parameters.AddWithValue("$due_at", after.DueAtUtc is null ? DBNull.Value : after.DueAtUtc.Value.ToUnixTimeMilliseconds());
                command.Parameters.AddWithValue("$due_zone", (object?)after.DueTimeZone ?? DBNull.Value);
                command.Parameters.AddWithValue("$archived", after.ArchivedAtUtc is null ? DBNull.Value : after.ArchivedAtUtc.Value.ToUnixTimeMilliseconds());
                command.Parameters.AddWithValue("$expected", target.ExpectedRevision);
                if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                    throw new SnookException(SnookErrorCode.RevisionConflict, "A selected task changed. No tasks were changed.");
                await PatchBatchTagsAsync(connection, transaction, after.Id, addTags, removeTags, cancellationToken);
                result.Add(after);
            }
            // Validate the final batch state: prerequisites selected in the same batch
            // may complete together, regardless of the caller's task order.
            if (update.Status == TaskState.Completed)
                foreach (var task in result)
                    if (await HasOpenPrerequisiteAsync(connection, transaction, task.Id, cancellationToken))
                        throw new SnookException(SnookErrorCode.DependencyBlocked, $"“{task.Title}” has an open prerequisite outside this selection. No tasks were changed.");
            // One committed batch notification, with the affected IDs and exact results in its durable receipt.
            await RecordMutationAsync(connection, transaction, operationId, "task-batch", operationId, "updated", 0, 1, nowUtc, cancellationToken);
            await RecordReceiptAsync(connection, transaction, operationId, operationId, 1, cancellationToken);
            return result;
        }, cancellationToken, new StoreWriteRequest(operationId, "BulkUpdateTasksAsync", new { tasks, update }));
    }

    private static string[] ValidateBatchTags(IReadOnlyList<string>? tags)
    {
        if (tags is null) return [];
        if (tags.Count > 50) throw new SnookException(SnookErrorCode.ValidationFailed, "Change at most 50 tags at a time.");
        return tags.Select(tag => Guard.Required(tag, "tag", 100)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static async Task EnsureBatchProjectAsync(SqliteConnection connection, SqliteTransaction transaction, Guid projectId, CancellationToken cancellationToken)
    {
        await EnsureProjectAsync(connection, transaction, projectId, cancellationToken);
        var project = await ReadProjectAsync(connection, transaction, projectId, false, cancellationToken);
        await EnsureActiveExistsAsync(connection, transaction, "boards", project!.BoardId, "Board", cancellationToken);
    }

    private static async Task PatchBatchTagsAsync(SqliteConnection connection, SqliteTransaction transaction, Guid taskId,
        string[] addTags, string[] removeTags, CancellationToken cancellationToken)
    {
        if (addTags.Length + removeTags.Length == 0) return;
        var workspaceId = await ReadWorkspaceIdForTaskAsync(connection, transaction, taskId, cancellationToken);
        foreach (var name in removeTags)
            await ExecuteMigrationCommandAsync(connection, transaction,
                "DELETE FROM task_tags WHERE task_id=$task AND tag_id IN (SELECT id FROM tags WHERE workspace_id=$workspace AND normalized_name=$name);",
                cancellationToken, ("$task", Id(taskId)), ("$workspace", Id(workspaceId)), ("$name", name.ToUpperInvariant()));
        foreach (var name in addTags)
        {
            var normalized = name.ToUpperInvariant();
            await ExecuteMigrationCommandAsync(connection, transaction,
                "INSERT OR IGNORE INTO tags(id,workspace_id,normalized_name,display_name,revision) VALUES($id,$workspace,$name,$display,1);",
                cancellationToken, ("$id", Id(Guid.NewGuid())), ("$workspace", Id(workspaceId)), ("$name", normalized), ("$display", name));
            await ExecuteMigrationCommandAsync(connection, transaction,
                "INSERT OR IGNORE INTO task_tags(id,task_id,tag_id,revision) SELECT $id,$task,id,1 FROM tags WHERE workspace_id=$workspace AND normalized_name=$name;",
                cancellationToken, ("$id", Id(Guid.NewGuid())), ("$task", Id(taskId)), ("$workspace", Id(workspaceId)), ("$name", normalized));
        }
    }
}
