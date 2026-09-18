using Process = System.Diagnostics.Process;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Snook.Domain;
using DomainCalendar = Snook.Domain.Calendar;

namespace Snook.Persistence.Sqlite;

public sealed record StoreState(
    Workspace Workspace,
    WorkspaceSettings Settings,
    IReadOnlyList<ActivityGroup> ActivityGroups,
    IReadOnlyList<Board> Boards,
    IReadOnlyList<Project> Projects,
    IReadOnlyList<Activity> Activities,
    IReadOnlyList<DomainCalendar> Calendars,
    IReadOnlyList<Tag> Tags,
    IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> TaskTagIds,
    IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> ProjectTagIds,
    IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> ActivityTagIds,
    IReadOnlyList<ScheduleBlock> ScheduleBlocks,
    IReadOnlyList<CalendarEvent> CalendarEvents,
    IReadOnlyList<CalendarEventOccurrenceOverride> CalendarEventExceptions,
    IReadOnlyList<TaskItem> Tasks,
    IReadOnlyList<TrackingSession> Sessions,
    long Cursor);

public sealed record StoreBackupResult(string Path, long Bytes, string Sha256, string? ManifestPath = null);

public sealed record StoreExportResult(string Path, long Bytes, string Sha256, int SchemaVersion);

public sealed partial class SqliteStore : IAsyncDisposable
{
    private static readonly JsonSerializerOptions ExportJsonOptions = new() { WriteIndented = true };
    private static readonly string SchemaChecksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(SchemaSql))).ToLowerInvariant();
    private readonly string _databasePath;
    private readonly string _leasePath;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private FileStream? _lease;
    private bool _initialized;

    public SqliteStore(string databasePath)
    {
        _databasePath = Path.GetFullPath(databasePath);
        _leasePath = _databasePath + ".owner";
    }

    public string DatabasePath => _databasePath;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        var directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        try
        {
            _lease = AcquireLease();
            await using var writer = new StreamWriter(_lease, leaveOpen: true);
            await writer.WriteAsync($"pid={Environment.ProcessId}{Environment.NewLine}started={DateTimeOffset.UtcNow:O}");
            await writer.FlushAsync(cancellationToken);
        }
        catch (IOException exception)
        {
            throw new SnookException(SnookErrorCode.StoreUnavailable, "The workspace is already open in another Snook host.", exception);
        }

        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = SchemaSql;
            await command.ExecuteNonQueryAsync(cancellationToken);
            await EnsureSchemaVersionAsync(connection, cancellationToken);
            await EnsureTaskWorkspaceMigrationAsync(connection, cancellationToken);
            await EnsureHabitsMigrationAsync(connection, cancellationToken);
            await EnsureJournalsMigrationAsync(connection, cancellationToken);
            await SeedAsync(connection, cancellationToken);
            await EnsureDefaultSettingsAsync(connection, cancellationToken);
            await EnsureDefaultCalendarAsync(connection, cancellationToken);
            await ValidateIntegrityAsync(connection, cancellationToken);
            await DetectRecoveryRequiredAsync(connection, DateTimeOffset.UtcNow, cancellationToken);
            _initialized = true;
        }
        catch
        {
            _lease?.Dispose();
            _lease = null;
            throw;
        }
    }

    private FileStream AcquireLease()
    {
        try
        {
            return OpenLeaseFile();
        }
        catch (IOException) when (TryRemoveStaleLease())
        {
            return OpenLeaseFile();
        }
    }

    private FileStream OpenLeaseFile()
        => new(_leasePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 128, FileOptions.DeleteOnClose);

    private bool TryRemoveStaleLease()
    {
        try
        {
            var marker = File.ReadAllText(_leasePath);
            var pidText = marker
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(line => line.StartsWith("pid=", StringComparison.Ordinal));
            if (pidText is null || !int.TryParse(pidText[4..], CultureInfo.InvariantCulture, out var pid) || IsProcessAlive(pid))
            {
                return false;
            }

            File.Delete(_leasePath);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public async Task<StoreState> LoadStateAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await using var connection = await OpenConnectionAsync(cancellationToken);

        var workspace = await ReadWorkspaceAsync(connection, cancellationToken);
        var settings = await ReadSettingsAsync(connection, workspace.Id, cancellationToken);
        var activityGroups = await ReadActivityGroupsAsync(connection, workspace.Id, includeDeleted: true, cancellationToken);
        var boards = await ReadBoardsAsync(connection, workspace.Id, includeDeleted: true, cancellationToken);
        var projects = await ReadProjectsAsync(connection, boards.Select(board => board.Id), includeDeleted: true, cancellationToken);
        var activities = await ReadActivitiesAsync(connection, workspace.Id, includeDeleted: true, cancellationToken);
        var calendars = await ReadCalendarsAsync(connection, workspace.Id, includeDeleted: true, cancellationToken);
        var tags = await ReadTagsAsync(connection, workspace.Id, cancellationToken);
        var taskTagIds = await ReadTaskTagIdsAsync(connection, cancellationToken);
        var projectTagIds = await ReadTagIdsAsync(connection, "project_tags", "project_id", cancellationToken);
        var activityTagIds = await ReadTagIdsAsync(connection, "activity_tags", "activity_id", cancellationToken);
        var scheduleBlocks = await ReadScheduleBlocksAsync(connection, calendars.Select(calendar => calendar.Id), includeDeleted: true, cancellationToken);
        var calendarEvents = await ReadCalendarEventsAsync(connection, calendars.Select(calendar => calendar.Id), includeDeleted: true, cancellationToken);
        var calendarEventExceptions = await ReadCalendarEventExceptionsAsync(connection, calendarEvents.Select(item => item.Id), cancellationToken);
        var tasks = await ReadTasksAsync(connection, projects.Select(project => project.Id), includeDeleted: true, cancellationToken);
        var sessions = await ReadSessionsAsync(connection, workspace.Id, cancellationToken);
        var cursor = await ReadCursorAsync(connection, cancellationToken);
        return new StoreState(workspace, settings, activityGroups, boards, projects, activities, calendars, tags, taskTagIds, projectTagIds, activityTagIds, scheduleBlocks, calendarEvents, calendarEventExceptions, tasks, sessions, cursor);
    }

    public async Task<WorkspaceSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var workspaceId = await ReadWorkspaceIdAsync(connection, null, cancellationToken);
        return await ReadSettingsAsync(connection, workspaceId, cancellationToken);
    }

    public async Task<WorkspaceSettings> UpdateSettingsAsync(WorkspaceSettings settings, long expectedRevision, Guid operationId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        return await WriteAsync(async (connection, transaction) =>
        {
            var workspaceId = await ReadWorkspaceIdAsync(connection, transaction, cancellationToken);
            if (await ReceiptExistsAsync(connection, transaction, operationId, cancellationToken))
            {
                return await ReadSettingsAsync(connection, workspaceId, cancellationToken);
            }

            var current = await ReadSettingsAsync(connection, workspaceId, cancellationToken);
            if (current.Revision != expectedRevision)
            {
                throw new SnookException(SnookErrorCode.RevisionConflict, "Workspace settings changed in another view. Refresh and try again.");
            }

            var nextRevision = current.Revision + 1;
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE workspace_settings SET allow_concurrent_foreground=$allow,revision=$revision WHERE workspace_id=$workspace AND revision=$expected;";
            command.Parameters.AddWithValue("$allow", settings.AllowConcurrentForeground ? 1 : 0);
            command.Parameters.AddWithValue("$revision", nextRevision);
            command.Parameters.AddWithValue("$workspace", Id(workspaceId));
            command.Parameters.AddWithValue("$expected", expectedRevision);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new SnookException(SnookErrorCode.RevisionConflict, "Workspace settings changed in another view. Refresh and try again.");
            }

            var updated = settings with { Revision = nextRevision };
            await RecordMutationAsync(connection, transaction, operationId, "workspace-settings", workspaceId, "updated", current.Revision, nextRevision, nowUtc, cancellationToken);
            await RecordReceiptAsync(connection, transaction, operationId, workspaceId, nextRevision, cancellationToken);
            return updated;
        }, cancellationToken);
    }

    public async Task<Board> CreateBoardAsync(string name, Guid operationId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        var validatedName = Guard.Required(name, "name", 200);
        return await WriteAsync(async (connection, transaction) =>
        {
            if (await ReceiptExistsAsync(connection, transaction, operationId, cancellationToken))
            {
                return await ReadBoardByOperationAsync(connection, transaction, operationId, cancellationToken);
            }

            var workspaceId = await ReadWorkspaceIdAsync(connection, transaction, cancellationToken);
            await EnsureNameAvailableAsync(connection, transaction, "boards", "workspace_id", workspaceId, validatedName, null, cancellationToken);
            var sortKey = await NextSortKeyAsync(connection, transaction, "boards", "workspace_id", workspaceId, cancellationToken);
            var board = new Board(Guid.NewGuid(), workspaceId, validatedName, sortKey, null, null, 1);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO boards(id,workspace_id,name,sort_key,revision) VALUES($id,$workspace,$name,$sort,1);";
            command.Parameters.AddWithValue("$id", Id(board.Id));
            command.Parameters.AddWithValue("$workspace", Id(board.WorkspaceId));
            command.Parameters.AddWithValue("$name", board.Name);
            command.Parameters.AddWithValue("$sort", board.SortKey);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await RecordMutationAsync(connection, transaction, operationId, "board", board.Id, "created", 0, 1, nowUtc, cancellationToken);
            await RecordReceiptAsync(connection, transaction, operationId, board.Id, 1, cancellationToken);
            return board;
        }, cancellationToken);
    }

    public async Task<Board> UpdateBoardAsync(Guid boardId, BoardUpdate update, long expectedRevision, Guid operationId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        var validatedName = Guard.Required(update.Name, "name", 200);
        return await WriteAsync(async (connection, transaction) =>
        {
            if (await ReceiptExistsAsync(connection, transaction, operationId, cancellationToken))
            {
                return await ReadBoardByOperationAsync(connection, transaction, operationId, cancellationToken);
            }

            var board = await ReadBoardAsync(connection, transaction, boardId, false, cancellationToken)
                ?? throw new SnookException(SnookErrorCode.NotFound, "Board was not found.");
            if (board.Revision != expectedRevision)
            {
                throw new SnookException(SnookErrorCode.RevisionConflict, "The board changed in another view. Refresh and try again.");
            }

            await EnsureNameAvailableAsync(connection, transaction, "boards", "workspace_id", board.WorkspaceId, validatedName, boardId, cancellationToken);
            var nextRevision = board.Revision + 1;
            await ExecuteOrganizationUpdateAsync(connection, transaction, "UPDATE boards SET name=$name, revision=$revision WHERE id=$id AND revision=$expected AND deleted_at_utc_ms IS NULL;", boardId, expectedRevision, nextRevision, nowUtc, validatedName, cancellationToken);
            var updated = board with { Name = validatedName, Revision = nextRevision };
            await RecordMutationAsync(connection, transaction, operationId, "board", boardId, "updated", board.Revision, nextRevision, nowUtc, cancellationToken);
            await RecordReceiptAsync(connection, transaction, operationId, boardId, nextRevision, cancellationToken);
            return updated;
        }, cancellationToken);
    }

    public async Task<Board> ReorderBoardAsync(Guid boardId, ReorderDirection direction, long expectedRevision, Guid operationId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        if (!Enum.IsDefined(direction))
        {
            throw new SnookException(SnookErrorCode.ValidationFailed, "The board reorder direction is invalid.");
        }

        return await WriteAsync(async (connection, transaction) =>
        {
            if (await ReceiptExistsAsync(connection, transaction, operationId, cancellationToken))
            {
                return await ReadBoardByOperationAsync(connection, transaction, operationId, cancellationToken);
            }

            var board = await ReadBoardAsync(connection, transaction, boardId, false, cancellationToken)
                ?? throw new SnookException(SnookErrorCode.NotFound, "Board was not found.");
            if (board.Revision != expectedRevision)
            {
                throw new SnookException(SnookErrorCode.RevisionConflict, "The board changed in another view. Refresh and try again.");
            }

            var neighbor = await FindNeighborSortKeyAsync(connection, transaction, "boards", "workspace_id", board.WorkspaceId, board.SortKey, direction, "AND archived_at_utc_ms IS NULL", cancellationToken);
            if (neighbor is null)
            {
                await RecordReceiptAsync(connection, transaction, operationId, board.Id, board.Revision, cancellationToken);
                return board;
            }

            var distance = Math.Abs(board.SortKey - neighbor.Value);
            var nextSortKey = direction == ReorderDirection.Earlier ? neighbor.Value - distance / 2m : neighbor.Value + distance / 2m;
            var nextRevision = board.Revision + 1;
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE boards SET sort_key=$sort, revision=$revision WHERE id=$id AND revision=$expected AND deleted_at_utc_ms IS NULL;";
            command.Parameters.AddWithValue("$sort", nextSortKey);
            command.Parameters.AddWithValue("$revision", nextRevision);
            command.Parameters.AddWithValue("$id", Id(boardId));
            command.Parameters.AddWithValue("$expected", expectedRevision);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new SnookException(SnookErrorCode.RevisionConflict, "The board changed in another view. Refresh and try again.");
            }

            var updated = board with { SortKey = nextSortKey, Revision = nextRevision };
            await RecordMutationAsync(connection, transaction, operationId, "board", boardId, "reordered", board.Revision, nextRevision, nowUtc, cancellationToken);
            await RecordReceiptAsync(connection, transaction, operationId, boardId, nextRevision, cancellationToken);
            return updated;
        }, cancellationToken);
    }

    public Task<Board> SetBoardArchivedAsync(Guid boardId, bool archived, long expectedRevision, Guid operationId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
        => SetBoardArchivedCoreAsync(boardId, archived, expectedRevision, operationId, nowUtc, cancellationToken);

    private async Task<Board> SetBoardArchivedCoreAsync(Guid boardId, bool archived, long expectedRevision, Guid operationId, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        EnsureInitialized();
        return await WriteAsync(async (connection, transaction) =>
        {
            if (await ReceiptExistsAsync(connection, transaction, operationId, cancellationToken))
            {
                return await ReadBoardByOperationAsync(connection, transaction, operationId, cancellationToken);
            }

            var board = await ReadBoardAsync(connection, transaction, boardId, false, cancellationToken)
                ?? throw new SnookException(SnookErrorCode.NotFound, "Board was not found.");
            if (board.Revision != expectedRevision)
            {
                throw new SnookException(SnookErrorCode.RevisionConflict, "The board changed in another view. Refresh and try again.");
            }

            var nextRevision = board.Revision + 1;
            await ExecuteOrganizationUpdateAsync(connection, transaction, "UPDATE boards SET archived_at_utc_ms=$archived, revision=$revision WHERE id=$id AND revision=$expected AND deleted_at_utc_ms IS NULL;", boardId, expectedRevision, nextRevision, nowUtc, archived ? nowUtc.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) : null, cancellationToken, valueIsTimestamp: true);
            var updated = board with { ArchivedAtUtc = archived ? (board.ArchivedAtUtc ?? nowUtc) : null, Revision = nextRevision };
            await RecordMutationAsync(connection, transaction, operationId, "board", boardId, archived ? "archived" : "restored", board.Revision, nextRevision, nowUtc, cancellationToken);
            await RecordReceiptAsync(connection, transaction, operationId, boardId, nextRevision, cancellationToken);
            return updated;
        }, cancellationToken);
    }

    public Task<Board> SetBoardDeletedAsync(Guid boardId, bool deleted, long expectedRevision, Guid operationId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
        => SetBoardDeletedCoreAsync(boardId, deleted, expectedRevision, operationId, nowUtc, cancellationToken);

    private async Task<Board> SetBoardDeletedCoreAsync(Guid boardId, bool deleted, long expectedRevision, Guid operationId, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        EnsureInitialized();
        return await WriteAsync(async (connection, transaction) =>
        {
            if (await ReceiptExistsAsync(connection, transaction, operationId, cancellationToken))
            {
                return await ReadBoardByOperationAsync(connection, transaction, operationId, cancellationToken);
            }

            var board = await ReadBoardAsync(connection, transaction, boardId, true, cancellationToken)
                ?? throw new SnookException(SnookErrorCode.NotFound, "Board was not found.");
            if (board.Revision != expectedRevision)
            {
                throw new SnookException(SnookErrorCode.RevisionConflict, "The board changed in another view. Refresh and try again.");
            }

            var nextRevision = board.Revision + 1;
            await ExecuteDeletedUpdateAsync(connection, transaction, "UPDATE boards SET deleted_at_utc_ms=$deleted, revision=$revision WHERE id=$id AND revision=$expected;", boardId, deleted ? nowUtc.ToUnixTimeMilliseconds() : null, expectedRevision, nextRevision, cancellationToken);
            var updated = board with { DeletedAtUtc = deleted ? (board.DeletedAtUtc ?? nowUtc) : null, Revision = nextRevision };
            await RecordMutationAsync(connection, transaction, operationId, "board", boardId, deleted ? "deleted" : "restored-deleted", board.Revision, nextRevision, nowUtc, cancellationToken);
            await RecordReceiptAsync(connection, transaction, operationId, boardId, nextRevision, cancellationToken);
            return updated;
        }, cancellationToken);
    }

    public async Task<Project> CreateProjectAsync(Guid boardId, string name, string description, bool starred, Guid operationId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        var validatedName = Guard.Required(name, "name", 200);
        var validatedDescription = string.IsNullOrWhiteSpace(description) ? string.Empty : Guard.Optional(description, "description", 20_000);
        return await WriteAsync(async (connection, transaction) =>
        {
            if (await ReceiptExistsAsync(connection, transaction, operationId, cancellationToken))
            {
                return await ReadProjectByOperationAsync(connection, transaction, operationId, cancellationToken);
            }

            await EnsureActiveExistsAsync(connection, transaction, "boards", boardId, "Board", cancellationToken);
            await EnsureNameAvailableAsync(connection, transaction, "projects", "board_id", boardId, validatedName, null, cancellationToken);
            var sortKey = await NextSortKeyAsync(connection, transaction, "projects", "board_id", boardId, cancellationToken);
            var project = new Project(Guid.NewGuid(), boardId, validatedName, validatedDescription, starred, sortKey, null, null, 1);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO projects(id,board_id,name,description,starred,sort_key,revision) VALUES($id,$board,$name,$description,$starred,$sort,1);";
            command.Parameters.AddWithValue("$id", Id(project.Id));
            command.Parameters.AddWithValue("$board", Id(project.BoardId));
            command.Parameters.AddWithValue("$name", project.Name);
            command.Parameters.AddWithValue("$description", project.Description);
            command.Parameters.AddWithValue("$starred", project.Starred ? 1 : 0);
            command.Parameters.AddWithValue("$sort", project.SortKey);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await RecordMutationAsync(connection, transaction, operationId, "project", project.Id, "created", 0, 1, nowUtc, cancellationToken);
            await RecordReceiptAsync(connection, transaction, operationId, project.Id, 1, cancellationToken);
            return project;
        }, cancellationToken);
    }

    public async Task<Project> UpdateProjectAsync(Guid projectId, ProjectUpdate update, long expectedRevision, Guid operationId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        var validatedName = Guard.Required(update.Name, "name", 200);
        var validatedDescription = string.IsNullOrWhiteSpace(update.Description) ? string.Empty : Guard.Optional(update.Description, "description", 20_000);
        return await WriteAsync(async (connection, transaction) =>
        {
            if (await ReceiptExistsAsync(connection, transaction, operationId, cancellationToken))
            {
                return await ReadProjectByOperationAsync(connection, transaction, operationId, cancellationToken);
            }

            var project = await ReadProjectAsync(connection, transaction, projectId, false, cancellationToken)
                ?? throw new SnookException(SnookErrorCode.NotFound, "Project was not found.");
            if (project.Revision != expectedRevision)
            {
                throw new SnookException(SnookErrorCode.RevisionConflict, "The project changed in another view. Refresh and try again.");
            }

            await EnsureNameAvailableAsync(connection, transaction, "projects", "board_id", project.BoardId, validatedName, projectId, cancellationToken);
            var nextRevision = project.Revision + 1;
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE projects SET name=$name, description=$description, starred=$starred, revision=$revision WHERE id=$id AND revision=$expected AND deleted_at_utc_ms IS NULL;";
            command.Parameters.AddWithValue("$name", validatedName);
            command.Parameters.AddWithValue("$description", validatedDescription);
            command.Parameters.AddWithValue("$starred", update.Starred ? 1 : 0);
            command.Parameters.AddWithValue("$revision", nextRevision);
            command.Parameters.AddWithValue("$id", Id(projectId));
            command.Parameters.AddWithValue("$expected", expectedRevision);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new SnookException(SnookErrorCode.RevisionConflict, "The project changed in another view. Refresh and try again.");
            }

            var updated = project with { Name = validatedName, Description = validatedDescription, Starred = update.Starred, Revision = nextRevision };
            await RecordMutationAsync(connection, transaction, operationId, "project", projectId, "updated", project.Revision, nextRevision, nowUtc, cancellationToken);
            await RecordReceiptAsync(connection, transaction, operationId, projectId, nextRevision, cancellationToken);
            return updated;
        }, cancellationToken);
    }

    public async Task<Project> ReorderProjectAsync(Guid projectId, ReorderDirection direction, long expectedRevision, Guid operationId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        if (!Enum.IsDefined(direction))
        {
            throw new SnookException(SnookErrorCode.ValidationFailed, "The project reorder direction is invalid.");
        }

        return await WriteAsync(async (connection, transaction) =>
        {
            if (await ReceiptExistsAsync(connection, transaction, operationId, cancellationToken))
            {
                return await ReadProjectByOperationAsync(connection, transaction, operationId, cancellationToken);
            }

            var project = await ReadProjectAsync(connection, transaction, projectId, false, cancellationToken)
                ?? throw new SnookException(SnookErrorCode.NotFound, "Project was not found.");
            if (project.Revision != expectedRevision)
            {
                throw new SnookException(SnookErrorCode.RevisionConflict, "The project changed in another view. Refresh and try again.");
            }

            var neighbor = await FindNeighborSortKeyAsync(connection, transaction, "projects", "board_id", project.BoardId, project.SortKey, direction, "AND archived_at_utc_ms IS NULL", cancellationToken);
            if (neighbor is null)
            {
                await RecordReceiptAsync(connection, transaction, operationId, project.Id, project.Revision, cancellationToken);
                return project;
            }

            var distance = Math.Abs(project.SortKey - neighbor.Value);
            var nextSortKey = direction == ReorderDirection.Earlier ? neighbor.Value - distance / 2m : neighbor.Value + distance / 2m;
            var nextRevision = project.Revision + 1;
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE projects SET sort_key=$sort, revision=$revision WHERE id=$id AND revision=$expected AND deleted_at_utc_ms IS NULL;";
            command.Parameters.AddWithValue("$sort", nextSortKey);
            command.Parameters.AddWithValue("$revision", nextRevision);
            command.Parameters.AddWithValue("$id", Id(projectId));
            command.Parameters.AddWithValue("$expected", expectedRevision);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new SnookException(SnookErrorCode.RevisionConflict, "The project changed in another view. Refresh and try again.");
            }

            var updated = project with { SortKey = nextSortKey, Revision = nextRevision };
            await RecordMutationAsync(connection, transaction, operationId, "project", projectId, "reordered", project.Revision, nextRevision, nowUtc, cancellationToken);
            await RecordReceiptAsync(connection, transaction, operationId, projectId, nextRevision, cancellationToken);
            return updated;
        }, cancellationToken);
    }

    public Task<Project> SetProjectArchivedAsync(Guid projectId, bool archived, long expectedRevision, Guid operationId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
        => SetProjectArchivedCoreAsync(projectId, archived, expectedRevision, operationId, nowUtc, cancellationToken);

    private async Task<Project> SetProjectArchivedCoreAsync(Guid projectId, bool archived, long expectedRevision, Guid operationId, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        EnsureInitialized();
        return await WriteAsync(async (connection, transaction) =>
        {
            if (await ReceiptExistsAsync(connection, transaction, operationId, cancellationToken))
            {
                return await ReadProjectByOperationAsync(connection, transaction, operationId, cancellationToken);
            }

            var project = await ReadProjectAsync(connection, transaction, projectId, false, cancellationToken)
                ?? throw new SnookException(SnookErrorCode.NotFound, "Project was not found.");
            if (project.Revision != expectedRevision)
            {
                throw new SnookException(SnookErrorCode.RevisionConflict, "The project changed in another view. Refresh and try again.");
            }

            var nextRevision = project.Revision + 1;
            await ExecuteOrganizationUpdateAsync(connection, transaction, "UPDATE projects SET archived_at_utc_ms=$archived, revision=$revision WHERE id=$id AND revision=$expected AND deleted_at_utc_ms IS NULL;", projectId, expectedRevision, nextRevision, nowUtc, archived ? nowUtc.ToUnixTimeMilliseconds() : null, cancellationToken, valueIsTimestamp: true);
            var updated = project with { ArchivedAtUtc = archived ? (project.ArchivedAtUtc ?? nowUtc) : null, Revision = nextRevision };
            await RecordMutationAsync(connection, transaction, operationId, "project", projectId, archived ? "archived" : "restored", project.Revision, nextRevision, nowUtc, cancellationToken);
            await RecordReceiptAsync(connection, transaction, operationId, projectId, nextRevision, cancellationToken);
            return updated;
        }, cancellationToken);
    }

    public Task<Project> SetProjectDeletedAsync(Guid projectId, bool deleted, long expectedRevision, Guid operationId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
        => SetProjectDeletedCoreAsync(projectId, deleted, expectedRevision, operationId, nowUtc, cancellationToken);

    private async Task<Project> SetProjectDeletedCoreAsync(Guid projectId, bool deleted, long expectedRevision, Guid operationId, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        EnsureInitialized();
        return await WriteAsync(async (connection, transaction) =>
        {
            if (await ReceiptExistsAsync(connection, transaction, operationId, cancellationToken))
            {
                return await ReadProjectByOperationAsync(connection, transaction, operationId, cancellationToken);
            }

            var project = await ReadProjectAsync(connection, transaction, projectId, true, cancellationToken)
                ?? throw new SnookException(SnookErrorCode.NotFound, "Project was not found.");
            if (project.Revision != expectedRevision)
            {
                throw new SnookException(SnookErrorCode.RevisionConflict, "The project changed in another view. Refresh and try again.");
            }

            var nextRevision = project.Revision + 1;
            await ExecuteDeletedUpdateAsync(connection, transaction, "UPDATE projects SET deleted_at_utc_ms=$deleted, revision=$revision WHERE id=$id AND revision=$expected;", projectId, deleted ? nowUtc.ToUnixTimeMilliseconds() : null, expectedRevision, nextRevision, cancellationToken);
            var updated = project with { DeletedAtUtc = deleted ? (project.DeletedAtUtc ?? nowUtc) : null, Revision = nextRevision };
            await RecordMutationAsync(connection, transaction, operationId, "project", projectId, deleted ? "deleted" : "restored-deleted", project.Revision, nextRevision, nowUtc, cancellationToken);
            await RecordReceiptAsync(connection, transaction, operationId, projectId, nextRevision, cancellationToken);
            return updated;
        }, cancellationToken);
    }

    public async Task<ActivityGroup> CreateActivityGroupAsync(string name, Guid operationId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        var validatedName = Guard.Required(name, "name", 200);
        return await WriteAsync(async (connection, transaction) =>
        {
            if (await ReceiptExistsAsync(connection, transaction, operationId, cancellationToken))
            {
                return await ReadActivityGroupByOperationAsync(connection, transaction, operationId, cancellationToken);
            }

            var workspaceId = await ReadWorkspaceIdAsync(connection, transaction, cancellationToken);
            await EnsureNameAvailableAsync(connection, transaction, "activity_groups", "workspace_id", workspaceId, validatedName, null, cancellationToken);
            var sortKey = await NextSortKeyAsync(connection, transaction, "activity_groups", "workspace_id", workspaceId, cancellationToken);
            var group = new ActivityGroup(Guid.NewGuid(), workspaceId, validatedName, sortKey, null, 1);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO activity_groups(id,workspace_id,name,sort_key,revision) VALUES($id,$workspace,$name,$sort_key,1);";
            command.Parameters.AddWithValue("$id", Id(group.Id));
            command.Parameters.AddWithValue("$workspace", Id(group.WorkspaceId));
            command.Parameters.AddWithValue("$name", group.Name);
            command.Parameters.AddWithValue("$sort_key", group.SortKey);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await RecordMutationAsync(connection, transaction, operationId, "activity-group", group.Id, "created", 0, 1, nowUtc, cancellationToken);
            await RecordReceiptAsync(connection, transaction, operationId, group.Id, 1, cancellationToken);
            return group;
        }, cancellationToken);
    }

    public async Task<ActivityGroup> UpdateActivityGroupAsync(Guid groupId, ActivityGroupUpdate update, long expectedRevision, Guid operationId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        var validatedName = Guard.Required(update.Name, "name", 200);
        return await WriteAsync(async (connection, transaction) =>
        {
            if (await ReceiptExistsAsync(connection, transaction, operationId, cancellationToken))
            {
                return await ReadActivityGroupByOperationAsync(connection, transaction, operationId, cancellationToken);
            }

            var group = await ReadActivityGroupAsync(connection, transaction, groupId, false, cancellationToken)
                ?? throw new SnookException(SnookErrorCode.NotFound, "Activity group was not found.");
            if (group.Revision != expectedRevision)
            {
                throw new SnookException(SnookErrorCode.RevisionConflict, "The activity group changed in another view. Refresh and try again.");
            }

            await EnsureNameAvailableAsync(connection, transaction, "activity_groups", "workspace_id", group.WorkspaceId, validatedName, groupId, cancellationToken);
            var nextRevision = group.Revision + 1;
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE activity_groups SET name=$name, revision=$revision WHERE id=$id AND revision=$expected AND deleted_at_utc_ms IS NULL;";
            command.Parameters.AddWithValue("$name", validatedName);
            command.Parameters.AddWithValue("$revision", nextRevision);
            command.Parameters.AddWithValue("$id", Id(groupId));
            command.Parameters.AddWithValue("$expected", expectedRevision);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new SnookException(SnookErrorCode.RevisionConflict, "The activity group changed in another view. Refresh and try again.");
            }

            var updated = group with { Name = validatedName, Revision = nextRevision };
            await RecordMutationAsync(connection, transaction, operationId, "activity-group", groupId, "updated", group.Revision, nextRevision, nowUtc, cancellationToken);
            await RecordReceiptAsync(connection, transaction, operationId, groupId, nextRevision, cancellationToken);
            return updated;
        }, cancellationToken);
    }

    public async Task<ActivityGroup> ReorderActivityGroupAsync(Guid groupId, ReorderDirection direction, long expectedRevision, Guid operationId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        if (!Enum.IsDefined(direction))
        {
            throw new SnookException(SnookErrorCode.ValidationFailed, "The activity group reorder direction is invalid.");
        }

        return await WriteAsync(async (connection, transaction) =>
        {
            if (await ReceiptExistsAsync(connection, transaction, operationId, cancellationToken))
            {
                return await ReadActivityGroupByOperationAsync(connection, transaction, operationId, cancellationToken);
            }

            var group = await ReadActivityGroupAsync(connection, transaction, groupId, false, cancellationToken)
                ?? throw new SnookException(SnookErrorCode.NotFound, "Activity group was not found.");
            if (group.Revision != expectedRevision)
            {
                throw new SnookException(SnookErrorCode.RevisionConflict, "The activity group changed in another view. Refresh and try again.");
            }

            var neighbor = await FindNeighborSortKeyAsync(connection, transaction, "activity_groups", "workspace_id", group.WorkspaceId, group.SortKey, direction, string.Empty, cancellationToken);
            if (neighbor is null)
            {
                await RecordReceiptAsync(connection, transaction, operationId, group.Id, group.Revision, cancellationToken);
                return group;
            }

            var distance = Math.Abs(group.SortKey - neighbor.Value);
            var nextSortKey = direction == ReorderDirection.Earlier ? neighbor.Value - distance / 2m : neighbor.Value + distance / 2m;
            var nextRevision = group.Revision + 1;
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE activity_groups SET sort_key=$sort, revision=$revision WHERE id=$id AND revision=$expected AND deleted_at_utc_ms IS NULL;";
            command.Parameters.AddWithValue("$sort", nextSortKey);
            command.Parameters.AddWithValue("$revision", nextRevision);
            command.Parameters.AddWithValue("$id", Id(groupId));
            command.Parameters.AddWithValue("$expected", expectedRevision);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new SnookException(SnookErrorCode.RevisionConflict, "The activity group changed in another view. Refresh and try again.");
            }

            var updated = group with { SortKey = nextSortKey, Revision = nextRevision };
            await RecordMutationAsync(connection, transaction, operationId, "activity-group", groupId, "reordered", group.Revision, nextRevision, nowUtc, cancellationToken);
            await RecordReceiptAsync(connection, transaction, operationId, groupId, nextRevision, cancellationToken);
            return updated;
        }, cancellationToken);
    }

    public Task<ActivityGroup> SetActivityGroupDeletedAsync(Guid groupId, bool deleted, long expectedRevision, Guid operationId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
        => SetActivityGroupDeletedCoreAsync(groupId, deleted, expectedRevision, operationId, nowUtc, cancellationToken);

    private async Task<ActivityGroup> SetActivityGroupDeletedCoreAsync(Guid groupId, bool deleted, long expectedRevision, Guid operationId, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        EnsureInitialized();
        return await WriteAsync(async (connection, transaction) =>
        {
            if (await ReceiptExistsAsync(connection, transaction, operationId, cancellationToken))
            {
                return await ReadActivityGroupByOperationAsync(connection, transaction, operationId, cancellationToken);
            }

            var group = await ReadActivityGroupAsync(connection, transaction, groupId, true, cancellationToken)
                ?? throw new SnookException(SnookErrorCode.NotFound, "Activity group was not found.");
            if (group.Revision != expectedRevision)
            {
                throw new SnookException(SnookErrorCode.RevisionConflict, "The activity group changed in another view. Refresh and try again.");
            }

            if (!deleted)
            {
                await EnsureNameAvailableAsync(connection, transaction, "activity_groups", "workspace_id", group.WorkspaceId, group.Name, groupId, cancellationToken);
            }

            var nextRevision = group.Revision + 1;
            await ExecuteDeletedUpdateAsync(connection, transaction, "UPDATE activity_groups SET deleted_at_utc_ms=$deleted, revision=$revision WHERE id=$id AND revision=$expected;", groupId, deleted ? nowUtc.ToUnixTimeMilliseconds() : null, expectedRevision, nextRevision, cancellationToken);
            var updated = group with { DeletedAtUtc = deleted ? (group.DeletedAtUtc ?? nowUtc) : null, Revision = nextRevision };
            await RecordMutationAsync(connection, transaction, operationId, "activity-group", groupId, deleted ? "deleted" : "restored-deleted", group.Revision, nextRevision, nowUtc, cancellationToken);
            await RecordReceiptAsync(connection, transaction, operationId, groupId, nextRevision, cancellationToken);
            return updated;
        }, cancellationToken);
    }

    public async Task<Activity> CreateActivityAsync(string name, string description, SessionLane defaultLane, Guid? groupId, Guid operationId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        var validatedName = Guard.Required(name, "name", 200);
        var validatedDescription = string.IsNullOrWhiteSpace(description) ? string.Empty : Guard.Optional(description, "description", 20_000);
        if (!Enum.IsDefined(defaultLane))
        {
            throw new SnookException(SnookErrorCode.ValidationFailed, "The activity lane is invalid.");
        }

        return await WriteAsync(async (connection, transaction) =>
        {
            if (await ReceiptExistsAsync(connection, transaction, operationId, cancellationToken))
            {
                return await ReadActivityByOperationAsync(connection, transaction, operationId, cancellationToken);
            }

            var workspaceId = await ReadWorkspaceIdAsync(connection, transaction, cancellationToken);
            if (groupId is not null)
            {
                await EnsureActivityGroupAsync(connection, transaction, groupId.Value, workspaceId, cancellationToken);
            }
            await EnsureNameAvailableAsync(connection, transaction, "activities", "workspace_id", workspaceId, validatedName, null, cancellationToken);
            var activity = new Activity(Guid.NewGuid(), workspaceId, validatedName, validatedDescription, defaultLane, null, null, 1, groupId);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO activities(id,workspace_id,name,description,lane_default,group_id,revision) VALUES($id,$workspace,$name,$description,$lane,$group,1);";
            command.Parameters.AddWithValue("$id", Id(activity.Id));
            command.Parameters.AddWithValue("$workspace", Id(activity.WorkspaceId));
            command.Parameters.AddWithValue("$name", activity.Name);
            command.Parameters.AddWithValue("$description", activity.Description);
            command.Parameters.AddWithValue("$lane", Lane(activity.DefaultLane));
            command.Parameters.AddWithValue("$group", groupId is null ? DBNull.Value : Id(groupId.Value));
            await command.ExecuteNonQueryAsync(cancellationToken);
            await RecordMutationAsync(connection, transaction, operationId, "activity", activity.Id, "created", 0, 1, nowUtc, cancellationToken);
            await RecordReceiptAsync(connection, transaction, operationId, activity.Id, 1, cancellationToken);
            return activity;
        }, cancellationToken);
    }

    public async Task<Activity> UpdateActivityAsync(Guid activityId, ActivityUpdate update, long expectedRevision, Guid operationId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        var validatedName = Guard.Required(update.Name, "name", 200);
        var validatedDescription = string.IsNullOrWhiteSpace(update.Description) ? string.Empty : Guard.Optional(update.Description, "description", 20_000);
        if (!Enum.IsDefined(update.DefaultLane))
        {
            throw new SnookException(SnookErrorCode.ValidationFailed, "The activity lane is invalid.");
        }

        return await WriteAsync(async (connection, transaction) =>
        {
            if (await ReceiptExistsAsync(connection, transaction, operationId, cancellationToken))
            {
                return await ReadActivityByOperationAsync(connection, transaction, operationId, cancellationToken);
            }

            var activity = await ReadActivityAsync(connection, transaction, activityId, false, cancellationToken)
                ?? throw new SnookException(SnookErrorCode.NotFound, "Activity was not found.");
            if (activity.Revision != expectedRevision)
            {
                throw new SnookException(SnookErrorCode.RevisionConflict, "The activity changed in another view. Refresh and try again.");
            }

            await EnsureNameAvailableAsync(connection, transaction, "activities", "workspace_id", activity.WorkspaceId, validatedName, activityId, cancellationToken);
            if (update.GroupId is not null)
            {
                await EnsureActivityGroupAsync(connection, transaction, update.GroupId.Value, activity.WorkspaceId, cancellationToken);
            }
            var nextRevision = activity.Revision + 1;
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE activities SET name=$name, description=$description, lane_default=$lane, group_id=$group, revision=$revision WHERE id=$id AND revision=$expected AND deleted_at_utc_ms IS NULL;";
            command.Parameters.AddWithValue("$name", validatedName);
            command.Parameters.AddWithValue("$description", validatedDescription);
            command.Parameters.AddWithValue("$lane", Lane(update.DefaultLane));
            command.Parameters.AddWithValue("$group", update.GroupId is null ? DBNull.Value : Id(update.GroupId.Value));
            command.Parameters.AddWithValue("$revision", nextRevision);
            command.Parameters.AddWithValue("$id", Id(activityId));
            command.Parameters.AddWithValue("$expected", expectedRevision);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new SnookException(SnookErrorCode.RevisionConflict, "The activity changed in another view. Refresh and try again.");
            }

            var updated = activity with { Name = validatedName, Description = validatedDescription, DefaultLane = update.DefaultLane, GroupId = update.GroupId, Revision = nextRevision };
            await RecordMutationAsync(connection, transaction, operationId, "activity", activityId, "updated", activity.Revision, nextRevision, nowUtc, cancellationToken);
            await RecordReceiptAsync(connection, transaction, operationId, activityId, nextRevision, cancellationToken);
            return updated;
        }, cancellationToken);
    }

    public Task<Activity> SetActivityArchivedAsync(Guid activityId, bool archived, long expectedRevision, Guid operationId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
        => SetActivityArchivedCoreAsync(activityId, archived, expectedRevision, operationId, nowUtc, cancellationToken);

    private async Task<Activity> SetActivityArchivedCoreAsync(Guid activityId, bool archived, long expectedRevision, Guid operationId, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        EnsureInitialized();
        return await WriteAsync(async (connection, transaction) =>
        {
            if (await ReceiptExistsAsync(connection, transaction, operationId, cancellationToken))
            {
                return await ReadActivityByOperationAsync(connection, transaction, operationId, cancellationToken);
            }

            var activity = await ReadActivityAsync(connection, transaction, activityId, false, cancellationToken)
                ?? throw new SnookException(SnookErrorCode.NotFound, "Activity was not found.");
            if (activity.Revision != expectedRevision)
            {
                throw new SnookException(SnookErrorCode.RevisionConflict, "The activity changed in another view. Refresh and try again.");
            }

            var nextRevision = activity.Revision + 1;
            await ExecuteOrganizationUpdateAsync(connection, transaction, "UPDATE activities SET archived_at_utc_ms=$archived, revision=$revision WHERE id=$id AND revision=$expected AND deleted_at_utc_ms IS NULL;", activityId, expectedRevision, nextRevision, nowUtc, archived ? nowUtc.ToUnixTimeMilliseconds() : null, cancellationToken, valueIsTimestamp: true);
            var updated = activity with { ArchivedAtUtc = archived ? (activity.ArchivedAtUtc ?? nowUtc) : null, Revision = nextRevision };
            await RecordMutationAsync(connection, transaction, operationId, "activity", activityId, archived ? "archived" : "restored", activity.Revision, nextRevision, nowUtc, cancellationToken);
            await RecordReceiptAsync(connection, transaction, operationId, activityId, nextRevision, cancellationToken);
            return updated;
        }, cancellationToken);
    }

    public Task<Activity> SetActivityDeletedAsync(Guid activityId, bool deleted, long expectedRevision, Guid operationId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
        => SetActivityDeletedCoreAsync(activityId, deleted, expectedRevision, operationId, nowUtc, cancellationToken);

    private async Task<Activity> SetActivityDeletedCoreAsync(Guid activityId, bool deleted, long expectedRevision, Guid operationId, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        EnsureInitialized();
        return await WriteAsync(async (connection, transaction) =>
        {
            if (await ReceiptExistsAsync(connection, transaction, operationId, cancellationToken))
            {
                return await ReadActivityByOperationAsync(connection, transaction, operationId, cancellationToken);
            }

            var activity = await ReadActivityAsync(connection, transaction, activityId, true, cancellationToken)
                ?? throw new SnookException(SnookErrorCode.NotFound, "Activity was not found.");
            if (activity.Revision != expectedRevision)
            {
                throw new SnookException(SnookErrorCode.RevisionConflict, "The activity changed in another view. Refresh and try again.");
            }

            var nextRevision = activity.Revision + 1;
            await ExecuteDeletedUpdateAsync(connection, transaction, "UPDATE activities SET deleted_at_utc_ms=$deleted, revision=$revision WHERE id=$id AND revision=$expected;", activityId, deleted ? nowUtc.ToUnixTimeMilliseconds() : null, expectedRevision, nextRevision, cancellationToken);
            var updated = activity with { DeletedAtUtc = deleted ? (activity.DeletedAtUtc ?? nowUtc) : null, Revision = nextRevision };
            await RecordMutationAsync(connection, transaction, operationId, "activity", activityId, deleted ? "deleted" : "restored-deleted", activity.Revision, nextRevision, nowUtc, cancellationToken);
            await RecordReceiptAsync(connection, transaction, operationId, activityId, nextRevision, cancellationToken);
            return updated;
        }, cancellationToken);
    }

    public async Task<TaskItem> CreateTaskAsync(
        Guid projectId,
        string title,
        Priority priority,
        DateOnly? dueDate,
        Guid operationId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        return await WriteAsync(async (connection, transaction) =>
        {
            if (await ReceiptExistsAsync(connection, transaction, operationId, cancellationToken))
            {
                return await ReadTaskByOperationAsync(connection, transaction, operationId, cancellationToken);
            }

            await EnsureProjectAsync(connection, transaction, projectId, cancellationToken);
            var task = new TaskItem(
                Guid.NewGuid(),
                projectId,
                Guard.Required(title, "title", 300),
                string.Empty,
                priority,
                TaskState.Open,
                dueDate,
                null,
                null,
                null,
                false,
                null,
                null,
                null,
                1);

            await InsertTaskAsync(connection, transaction, task, nowUtc, cancellationToken);
            await RecordMutationAsync(connection, transaction, operationId, "task", task.Id, "created", 0, 1, nowUtc, cancellationToken);
            await RecordReceiptAsync(connection, transaction, operationId, task.Id, 1, cancellationToken);
            return task;
        }, cancellationToken);
    }

    public async Task<TaskItem> UpdateTaskAsync(
        Guid taskId,
        TaskUpdate update,
        long expectedRevision,
        Guid operationId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        var title = Guard.Required(update.Title, "title", 300);
        var description = string.IsNullOrWhiteSpace(update.Description) ? string.Empty : Guard.Optional(update.Description, "description", 20_000);
        if (!Enum.IsDefined(update.Priority))
        {
            throw new SnookException(SnookErrorCode.ValidationFailed, "The task priority is invalid.");
        }

        return await WriteAsync(async (connection, transaction) =>
        {
            if (await ReceiptExistsAsync(connection, transaction, operationId, cancellationToken))
            {
                return await ReadTaskByOperationAsync(connection, transaction, operationId, cancellationToken);
            }

            var task = await ReadTaskAsync(connection, transaction, taskId, cancellationToken)
                ?? throw new SnookException(SnookErrorCode.NotFound, "Task was not found.");
            if (task.ArchivedAtUtc is not null)
            {
                throw new SnookException(SnookErrorCode.InvalidTransition, "Archived tasks must be restored before editing.");
            }

            if (task.Revision != expectedRevision)
            {
                throw new SnookException(SnookErrorCode.RevisionConflict, "The task changed in another view. Refresh and try again.");
            }

            if (update.DefaultActivityId is not null)
            {
                await EnsureActivityAsync(connection, transaction, update.DefaultActivityId.Value, cancellationToken);
            }

            var nextRevision = task.Revision + 1;
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE tasks SET title=$title, description=$description, priority=$priority,
                    due_date=$due_date,
                    due_at_utc_ms=CASE WHEN $due_date IS NOT NULL THEN NULL ELSE due_at_utc_ms END,
                    due_time_zone=CASE WHEN $due_date IS NOT NULL THEN NULL ELSE due_time_zone END,
                    default_activity_id=$activity, starred=$starred,
                    updated_at_utc_ms=$updated, revision=$revision
                WHERE id=$id AND revision=$expected AND deleted_at_utc_ms IS NULL;
                """;
            command.Parameters.AddWithValue("$title", title);
            command.Parameters.AddWithValue("$description", description);
            command.Parameters.AddWithValue("$priority", (int)update.Priority);
            command.Parameters.AddWithValue("$due_date", update.DueDate is null ? DBNull.Value : update.DueDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$activity", update.DefaultActivityId is null ? DBNull.Value : Id(update.DefaultActivityId.Value));
            command.Parameters.AddWithValue("$starred", update.Starred ? 1 : 0);
            command.Parameters.AddWithValue("$updated", nowUtc.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$revision", nextRevision);
            command.Parameters.AddWithValue("$id", Id(taskId));
            command.Parameters.AddWithValue("$expected", expectedRevision);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new SnookException(SnookErrorCode.RevisionConflict, "The task changed in another view. Refresh and try again.");
            }

            var updated = task with
            {
                Title = title,
                Description = description,
                Priority = update.Priority,
                DueDate = update.DueDate,
                DueAtUtc = update.DueDate is not null ? null : task.DueAtUtc,
                DueTimeZone = update.DueDate is not null ? null : task.DueTimeZone,
                DefaultActivityId = update.DefaultActivityId,
                Starred = update.Starred,
                Revision = nextRevision
            };
            await RecordMutationAsync(connection, transaction, operationId, "task", task.Id, "updated", task.Revision, nextRevision, nowUtc, cancellationToken);
            await RecordReceiptAsync(connection, transaction, operationId, task.Id, nextRevision, cancellationToken);
            return updated;
        }, cancellationToken);
    }

    public async Task<TaskItem> MoveTaskAsync(Guid taskId, Guid projectId, long expectedRevision, Guid operationId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        return await WriteAsync(async (connection, transaction) =>
        {
            if (await ReceiptExistsAsync(connection, transaction, operationId, cancellationToken))
            {
                return await ReadTaskByOperationAsync(connection, transaction, operationId, cancellationToken);
            }

            await EnsureProjectAsync(connection, transaction, projectId, cancellationToken);
            var task = await ReadTaskAsync(connection, transaction, taskId, cancellationToken)
                ?? throw new SnookException(SnookErrorCode.NotFound, "Task was not found.");
            if (task.ArchivedAtUtc is not null)
            {
                throw new SnookException(SnookErrorCode.InvalidTransition, "Archived tasks must be restored before moving.");
            }

            if (task.Revision != expectedRevision)
            {
                throw new SnookException(SnookErrorCode.RevisionConflict, "The task changed in another view. Refresh and try again.");
            }

            var nextRevision = task.Revision + 1;
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE tasks SET project_id=$project, updated_at_utc_ms=$updated, revision=$revision WHERE id=$id AND revision=$expected AND deleted_at_utc_ms IS NULL;";
            command.Parameters.AddWithValue("$project", Id(projectId));
            command.Parameters.AddWithValue("$updated", nowUtc.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$revision", nextRevision);
            command.Parameters.AddWithValue("$id", Id(taskId));
            command.Parameters.AddWithValue("$expected", expectedRevision);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new SnookException(SnookErrorCode.RevisionConflict, "The task changed in another view. Refresh and try again.");
            }

            var updated = task with { ProjectId = projectId, Revision = nextRevision };
            await RecordMutationAsync(connection, transaction, operationId, "task", task.Id, "moved", task.Revision, nextRevision, nowUtc, cancellationToken);
            await RecordReceiptAsync(connection, transaction, operationId, task.Id, nextRevision, cancellationToken);
            return updated;
        }, cancellationToken);
    }

    public Task<TaskItem> SetTaskArchivedAsync(Guid taskId, bool archived, long expectedRevision, Guid operationId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
        => SetTaskSoftStateAsync(taskId, archived, null, expectedRevision, operationId, nowUtc, cancellationToken);

    public Task<TaskItem> SetTaskDeletedAsync(Guid taskId, bool deleted, long expectedRevision, Guid operationId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
        => SetTaskSoftStateAsync(taskId, null, deleted, expectedRevision, operationId, nowUtc, cancellationToken);

    private async Task<TaskItem> SetTaskSoftStateAsync(Guid taskId, bool? archived, bool? deleted, long expectedRevision, Guid operationId, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        EnsureInitialized();
        return await WriteAsync(async (connection, transaction) =>
        {
            if (await ReceiptExistsAsync(connection, transaction, operationId, cancellationToken))
            {
                return await ReadTaskByOperationAsync(connection, transaction, operationId, cancellationToken);
            }

            var task = await ReadTaskAnyAsync(connection, transaction, taskId, cancellationToken)
                ?? throw new SnookException(SnookErrorCode.NotFound, "Task was not found.");
            if (task.Revision != expectedRevision)
            {
                throw new SnookException(SnookErrorCode.RevisionConflict, "The task changed in another view. Refresh and try again.");
            }

            var currentArchived = task.ArchivedAtUtc is not null;
            var currentDeleted = task.DeletedAtUtc is not null;
            if (currentDeleted && deleted is null)
            {
                throw new SnookException(SnookErrorCode.InvalidTransition, "Restore the deleted task before changing its archive state.");
            }

            var nextRevision = task.Revision + 1;
            var archivedAt = archived is null ? task.ArchivedAtUtc : archived.Value ? (task.ArchivedAtUtc ?? nowUtc) : null;
            var deletedAt = deleted is null ? task.DeletedAtUtc : deleted.Value ? (task.DeletedAtUtc ?? nowUtc) : null;

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE tasks SET archived_at_utc_ms=$archived, deleted_at_utc_ms=$deleted, updated_at_utc_ms=$updated, revision=$revision WHERE id=$id AND revision=$expected;";
            command.Parameters.AddWithValue("$archived", archivedAt is null ? DBNull.Value : archivedAt.Value.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$deleted", deletedAt is null ? DBNull.Value : deletedAt.Value.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$updated", nowUtc.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$revision", nextRevision);
            command.Parameters.AddWithValue("$id", Id(taskId));
            command.Parameters.AddWithValue("$expected", expectedRevision);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new SnookException(SnookErrorCode.RevisionConflict, "The task changed in another view. Refresh and try again.");
            }

            var kind = deleted is true ? "deleted" : deleted is false ? "restored" : archived is true ? "archived" : "unarchived";
            var updated = task with { ArchivedAtUtc = archivedAt, DeletedAtUtc = deletedAt, Revision = nextRevision };
            await RecordMutationAsync(connection, transaction, operationId, "task", task.Id, kind, task.Revision, nextRevision, nowUtc, cancellationToken);
            await RecordReceiptAsync(connection, transaction, operationId, task.Id, nextRevision, cancellationToken);
            return updated;
        }, cancellationToken);
    }

    public async Task<TaskDetails> GetTaskDetailsAsync(Guid taskId, DateTimeOffset? nowUtc = null, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var task = await ReadTaskAsync(connection, null, taskId, cancellationToken)
            ?? throw new SnookException(SnookErrorCode.NotFound, "Task was not found.");
        var tags = await ReadTaskTagsAsync(connection, taskId, cancellationToken);
        var links = await ReadTaskLinksAsync(connection, taskId, cancellationToken);
        var prerequisites = await ReadPrerequisiteIdsAsync(connection, taskId, cancellationToken);
        var workspace = await ReadWorkspaceAsync(connection, cancellationToken);
        var sessions = (await ReadSessionsAsync(connection, workspace.Id, cancellationToken)).Where(session => session.TaskId == taskId).ToArray();
        var asOfUtc = nowUtc ?? DateTimeOffset.UtcNow;
        var tracked = sessions.SelectMany(session => session.Intervals).Sum(interval => TimeMath.DurationMilliseconds([interval], asOfUtc));
        var active = sessions.Where(session => session.State == SessionState.Running).SelectMany(session => session.Intervals.Where(interval => interval.EndedAtUtc is null)).Sum(interval => TimeMath.DurationMilliseconds([interval], asOfUtc));
        return new TaskDetails(task, tags, links, prerequisites, tracked, active);
    }

    public async Task<TaskLink> AddTaskLinkAsync(Guid taskId, string? label, string uri, string kind, Guid operationId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        var validatedUri = Guard.Required(uri, "uri", 4_000);
        if (!Uri.TryCreate(validatedUri, UriKind.Absolute, out var parsedUri)
            || parsedUri.Scheme is not ("http" or "https" or "file"))
        {
            throw new SnookException(SnookErrorCode.ValidationFailed, "Links must be absolute HTTP, HTTPS, or file URIs.");
        }

        return await WriteAsync(async (connection, transaction) =>
        {
            if (await ReceiptExistsAsync(connection, transaction, operationId, cancellationToken))
            {
                return await ReadTaskLinkByOperationAsync(connection, transaction, operationId, cancellationToken);
            }

            await EnsureTaskAsync(connection, transaction, taskId, cancellationToken);
            var link = new TaskLink(Guid.NewGuid(), taskId, string.IsNullOrWhiteSpace(label) ? null : Guard.Optional(label, "label", 300), validatedUri, Guard.Required(kind, "kind", 100));
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO task_links(id,task_id,label,uri,kind,created_at_utc_ms,revision) VALUES($id,$task,$label,$uri,$kind,$created,1);";
            command.Parameters.AddWithValue("$id", Id(link.Id));
            command.Parameters.AddWithValue("$task", Id(taskId));
            command.Parameters.AddWithValue("$label", link.Label is null ? DBNull.Value : link.Label);
            command.Parameters.AddWithValue("$uri", link.Uri);
            command.Parameters.AddWithValue("$kind", link.Kind);
            command.Parameters.AddWithValue("$created", nowUtc.ToUnixTimeMilliseconds());
            await command.ExecuteNonQueryAsync(cancellationToken);
            await RecordMutationAsync(connection, transaction, operationId, "task-link", link.Id, "created", 0, 1, nowUtc, cancellationToken);
            await RecordReceiptAsync(connection, transaction, operationId, link.Id, 1, cancellationToken);
            return link;
        }, cancellationToken);
    }

    public async Task<Tag> AddTaskTagAsync(Guid taskId, string displayName, string? color, Guid operationId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        var name = Guard.Required(displayName, "displayName", 100);
        var normalized = name.ToUpperInvariant();
        return await WriteAsync(async (connection, transaction) =>
        {
            if (await ReceiptExistsAsync(connection, transaction, operationId, cancellationToken))
            {
                return await ReadTagByOperationAsync(connection, transaction, operationId, cancellationToken);
            }

            var workspaceId = await ReadWorkspaceIdForTaskAsync(connection, transaction, taskId, cancellationToken);
            var tag = await ReadTagByNameAsync(connection, transaction, workspaceId, normalized, cancellationToken);
            if (tag is null)
            {
                tag = new Tag(Guid.NewGuid(), workspaceId, normalized, name, string.IsNullOrWhiteSpace(color) ? null : Guard.Optional(color, "color", 32), 1);
                await using var insertTag = connection.CreateCommand();
                insertTag.Transaction = transaction;
                insertTag.CommandText = "INSERT INTO tags(id,workspace_id,normalized_name,display_name,color,revision) VALUES($id,$workspace,$normalized,$display,$color,1);";
                insertTag.Parameters.AddWithValue("$id", Id(tag.Id));
                insertTag.Parameters.AddWithValue("$workspace", Id(workspaceId));
                insertTag.Parameters.AddWithValue("$normalized", tag.NormalizedName);
                insertTag.Parameters.AddWithValue("$display", tag.DisplayName);
                insertTag.Parameters.AddWithValue("$color", tag.Color is null ? DBNull.Value : tag.Color);
                await insertTag.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var membership = connection.CreateCommand();
            membership.Transaction = transaction;
            membership.CommandText = "INSERT OR IGNORE INTO task_tags(id,task_id,tag_id,revision) VALUES($id,$task,$tag,1);";
            membership.Parameters.AddWithValue("$id", Id(Guid.NewGuid()));
            membership.Parameters.AddWithValue("$task", Id(taskId));
            membership.Parameters.AddWithValue("$tag", Id(tag.Id));
            await membership.ExecuteNonQueryAsync(cancellationToken);
            await RecordMutationAsync(connection, transaction, operationId, "task", taskId, "tag-added", 0, 1, nowUtc, cancellationToken);
            await RecordReceiptAsync(connection, transaction, operationId, tag.Id, tag.Revision, cancellationToken);
            return tag;
        }, cancellationToken);
    }

    public Task<Tag> AddProjectTagAsync(Guid projectId, string displayName, string? color, Guid operationId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
        => AddOrganizationTagAsync(projectId, "project_tags", "project_id", "project", displayName, color, operationId, nowUtc, cancellationToken);

    public Task<Tag> AddActivityTagAsync(Guid activityId, string displayName, string? color, Guid operationId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
        => AddOrganizationTagAsync(activityId, "activity_tags", "activity_id", "activity", displayName, color, operationId, nowUtc, cancellationToken);

    private async Task<Tag> AddOrganizationTagAsync(Guid entityId, string membershipTable, string entityColumn, string aggregateType, string displayName, string? color, Guid operationId, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        EnsureInitialized();
        var name = Guard.Required(displayName, "displayName", 100);
        var normalized = name.ToUpperInvariant();
        return await WriteAsync(async (connection, transaction) =>
        {
            if (await ReceiptExistsAsync(connection, transaction, operationId, cancellationToken))
            {
                return await ReadTagByOperationAsync(connection, transaction, operationId, cancellationToken);
            }

            var workspaceId = aggregateType switch
            {
                "project" => await ReadWorkspaceIdForProjectAsync(connection, transaction, entityId, cancellationToken),
                "activity" => await ReadWorkspaceIdForActivityAsync(connection, transaction, entityId, cancellationToken),
                _ => throw new SnookException(SnookErrorCode.InternalError, "Unsupported tag target.")
            };
            var tag = await ReadTagByNameAsync(connection, transaction, workspaceId, normalized, cancellationToken);
            if (tag is null)
            {
                tag = new Tag(Guid.NewGuid(), workspaceId, normalized, name, string.IsNullOrWhiteSpace(color) ? null : Guard.Optional(color, "color", 32), 1);
                await using var insertTag = connection.CreateCommand();
                insertTag.Transaction = transaction;
                insertTag.CommandText = "INSERT INTO tags(id,workspace_id,normalized_name,display_name,color,revision) VALUES($id,$workspace,$normalized,$display,$color,1);";
                insertTag.Parameters.AddWithValue("$id", Id(tag.Id));
                insertTag.Parameters.AddWithValue("$workspace", Id(workspaceId));
                insertTag.Parameters.AddWithValue("$normalized", tag.NormalizedName);
                insertTag.Parameters.AddWithValue("$display", tag.DisplayName);
                insertTag.Parameters.AddWithValue("$color", tag.Color is null ? DBNull.Value : tag.Color);
                await insertTag.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var membership = connection.CreateCommand();
            membership.Transaction = transaction;
            membership.CommandText = $"INSERT OR IGNORE INTO {membershipTable}(id,{entityColumn},tag_id,revision) VALUES($id,$entity,$tag,1);";
            membership.Parameters.AddWithValue("$id", Id(Guid.NewGuid()));
            membership.Parameters.AddWithValue("$entity", Id(entityId));
            membership.Parameters.AddWithValue("$tag", Id(tag.Id));
            await membership.ExecuteNonQueryAsync(cancellationToken);
            await RecordMutationAsync(connection, transaction, operationId, aggregateType, entityId, "tag-added", 0, 1, nowUtc, cancellationToken);
            await RecordReceiptAsync(connection, transaction, operationId, tag.Id, tag.Revision, cancellationToken);
            return tag;
        }, cancellationToken);
    }

    public async Task<TaskItem> SetTaskCompletionAsync(
        Guid taskId,
        bool completed,
        long expectedRevision,
        Guid operationId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        return await WriteAsync(async (connection, transaction) =>
        {
            if (await ReceiptExistsAsync(connection, transaction, operationId, cancellationToken))
            {
                return await ReadTaskByOperationAsync(connection, transaction, operationId, cancellationToken);
            }

            var task = await ReadTaskAsync(connection, transaction, taskId, cancellationToken)
                ?? throw new SnookException(SnookErrorCode.NotFound, "Task was not found.");
            if (task.Revision != expectedRevision)
            {
                throw new SnookException(SnookErrorCode.RevisionConflict, "The task changed in another view. Refresh and try again.");
            }

            if (completed && await HasOpenPrerequisiteAsync(connection, transaction, taskId, cancellationToken))
            {
                throw new SnookException(SnookErrorCode.DependencyBlocked, "Complete the task's prerequisites before completing this task.");
            }

            var newRevision = task.Revision + 1;
            DateTimeOffset? completedAt = completed ? nowUtc : null;
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE tasks SET status = $status, completed_at_utc_ms = $completed_at,
                    updated_at_utc_ms = $updated_at, revision = $revision
                WHERE id = $id AND revision = $expected;
                """;
            command.Parameters.AddWithValue("$status", completed ? "completed" : "open");
            command.Parameters.AddWithValue("$completed_at", completedAt is null ? DBNull.Value : completedAt.Value.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$updated_at", nowUtc.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$revision", newRevision);
            command.Parameters.AddWithValue("$id", Id(taskId));
            command.Parameters.AddWithValue("$expected", expectedRevision);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new SnookException(SnookErrorCode.RevisionConflict, "The task changed in another view. Refresh and try again.");
            }

            var updated = task with { Status = completed ? TaskState.Completed : TaskState.Open, CompletedAtUtc = completedAt, Revision = newRevision };
            await RecordMutationAsync(connection, transaction, operationId, "task", task.Id, completed ? "completed" : "reopened", task.Revision, newRevision, nowUtc, cancellationToken);
            await RecordReceiptAsync(connection, transaction, operationId, task.Id, newRevision, cancellationToken);
            return updated;
        }, cancellationToken);
    }

    public async Task AddTaskDependencyAsync(Guid taskId, Guid prerequisiteTaskId, Guid operationId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await WriteAsync(async (connection, transaction) =>
        {
            if (await ReceiptExistsAsync(connection, transaction, operationId, cancellationToken))
            {
                return 0;
            }

            if (taskId == prerequisiteTaskId)
            {
                throw new SnookException(SnookErrorCode.ValidationFailed, "A task cannot depend on itself.");
            }

            await EnsureTaskAsync(connection, transaction, taskId, cancellationToken);
            await EnsureTaskAsync(connection, transaction, prerequisiteTaskId, cancellationToken);
            if (await WouldCreateDependencyCycleAsync(connection, transaction, taskId, prerequisiteTaskId, cancellationToken))
            {
                throw new SnookException(SnookErrorCode.ValidationFailed, "That dependency would create a cycle.");
            }

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT OR IGNORE INTO task_dependencies(id,task_id,prerequisite_task_id,created_at_utc_ms,revision) VALUES($id,$task,$prerequisite,$created,1);";
            command.Parameters.AddWithValue("$id", Id(Guid.NewGuid()));
            command.Parameters.AddWithValue("$task", Id(taskId));
            command.Parameters.AddWithValue("$prerequisite", Id(prerequisiteTaskId));
            command.Parameters.AddWithValue("$created", nowUtc.ToUnixTimeMilliseconds());
            await command.ExecuteNonQueryAsync(cancellationToken);
            await RecordMutationAsync(connection, transaction, operationId, "task", taskId, "dependency-added", 0, 1, nowUtc, cancellationToken);
            await RecordReceiptAsync(connection, transaction, operationId, taskId, 1, cancellationToken);
            return 0;
        }, cancellationToken);
    }

    public async Task<TrackingSession> StartSessionAsync(
        Guid? taskId,
        Guid? activityId,
        SessionLane lane,
        Guid operationId,
        DateTimeOffset nowUtc,
        bool allowConcurrentForeground,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        if (taskId is null && activityId is null)
        {
            throw new SnookException(SnookErrorCode.ValidationFailed, "A session needs a task or activity target.");
        }

        return await WriteAsync(async (connection, transaction) =>
        {
            if (await ReceiptExistsAsync(connection, transaction, operationId, cancellationToken))
            {
                return await ReadSessionByOperationAsync(connection, transaction, operationId, cancellationToken);
            }

            if (taskId is not null)
            {
                var task = await ReadTaskAsync(connection, transaction, taskId.Value, cancellationToken)
                    ?? throw new SnookException(SnookErrorCode.NotFound, "Task was not found.");
                if (task.DeletedAtUtc is not null || task.ArchivedAtUtc is not null)
                {
                    throw new SnookException(SnookErrorCode.InvalidTransition, "Archived or deleted tasks cannot be timed.");
                }
            }

            if (activityId is not null)
            {
                await EnsureActivityAsync(connection, transaction, activityId.Value, cancellationToken);
            }

            if (lane == SessionLane.Foreground && !allowConcurrentForeground)
            {
                await PauseRunningForegroundAsync(connection, transaction, nowUtc, cancellationToken);
            }

            var session = new TrackingSession(
                Guid.NewGuid(),
                await ReadWorkspaceIdAsync(connection, transaction, cancellationToken),
                taskId,
                activityId,
                lane,
                SessionState.Running,
                nowUtc,
                null,
                string.Empty,
                nowUtc,
                nowUtc,
                1,
                [new TimeInterval(Guid.NewGuid(), nowUtc, null, "timer")]);

            await InsertSessionAsync(connection, transaction, session, cancellationToken);
            await RecordMutationAsync(connection, transaction, operationId, "session", session.Id, "started", 0, 1, nowUtc, cancellationToken);
            await RecordReceiptAsync(connection, transaction, operationId, session.Id, 1, cancellationToken);
            return session;
        }, cancellationToken);
    }

    public Task<TrackingSession> TransitionSessionAsync(
        Guid sessionId,
        SessionState targetState,
        long expectedRevision,
        Guid operationId,
        DateTimeOffset nowUtc,
        string? notes = null,
        bool pauseOtherForeground = false,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        return WriteAsync(async (connection, transaction) =>
        {
            if (await ReceiptExistsAsync(connection, transaction, operationId, cancellationToken))
            {
                return await ReadSessionByOperationAsync(connection, transaction, operationId, cancellationToken);
            }

            var session = await ReadSessionAsync(connection, transaction, sessionId, cancellationToken)
                ?? throw new SnookException(SnookErrorCode.NotFound, "Session was not found.");
            if (session.Revision != expectedRevision)
            {
                throw new SnookException(SnookErrorCode.RevisionConflict, "The timer changed in another view. Refresh and try again.");
            }

            var valid = (session.State, targetState) switch
            {
                (SessionState.Running, SessionState.Paused) => true,
                (SessionState.Running, SessionState.Stopped) => true,
                (SessionState.Paused, SessionState.Running) => true,
                (SessionState.Paused, SessionState.Stopped) => true,
                _ => false
            };
            if (!valid)
            {
                throw new SnookException(SnookErrorCode.InvalidTransition, $"Cannot move a {session.State} session to {targetState}.");
            }

            if (targetState == SessionState.Running && session.Intervals.Any(interval => interval.EndedAtUtc is null))
            {
                throw new SnookException(SnookErrorCode.InvalidTransition, "The session already has an open interval.");
            }

            var nextRevision = session.Revision + 1;
            if (session.State == SessionState.Running)
            {
                await CloseOpenIntervalAsync(connection, transaction, sessionId, nowUtc, cancellationToken);
            }

            if (targetState == SessionState.Running)
            {
                if (session.Lane == SessionLane.Foreground && pauseOtherForeground)
                {
                    await PauseRunningForegroundAsync(connection, transaction, nowUtc, cancellationToken);
                }
                await InsertIntervalAsync(connection, transaction, sessionId, new TimeInterval(Guid.NewGuid(), nowUtc, null, "timer"), cancellationToken);
            }

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE tracking_sessions SET state = $state, stopped_at_utc_ms = $stopped,
                    notes = COALESCE($notes, notes), updated_at_utc_ms = $updated, revision = $revision
                WHERE id = $id AND revision = $expected;
                """;
            command.Parameters.AddWithValue("$state", State(targetState));
            command.Parameters.AddWithValue("$stopped", targetState == SessionState.Stopped ? nowUtc.ToUnixTimeMilliseconds() : DBNull.Value);
            command.Parameters.AddWithValue("$notes", notes is null ? DBNull.Value : Guard.Optional(notes, "notes"));
            command.Parameters.AddWithValue("$updated", nowUtc.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$revision", nextRevision);
            command.Parameters.AddWithValue("$id", Id(sessionId));
            command.Parameters.AddWithValue("$expected", expectedRevision);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new SnookException(SnookErrorCode.RevisionConflict, "The timer changed in another view. Refresh and try again.");
            }

            var updated = await ReadSessionAsync(connection, transaction, sessionId, cancellationToken)
                ?? throw new SnookException(SnookErrorCode.InternalError, "Session disappeared during transition.");
            await RecordMutationAsync(connection, transaction, operationId, "session", sessionId, targetState.ToString().ToLowerInvariant(), session.Revision, nextRevision, nowUtc, cancellationToken);
            await RecordReceiptAsync(connection, transaction, operationId, sessionId, nextRevision, cancellationToken);
            return updated;
        }, cancellationToken);
    }

    public async Task<TrackingSession> ResolveRecoveryAsync(Guid sessionId, RecoveryDecision decision, long expectedRevision, Guid operationId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        return await WriteAsync(async (connection, transaction) =>
        {
            if (await ReceiptExistsAsync(connection, transaction, operationId, cancellationToken))
            {
                return await ReadSessionByOperationAsync(connection, transaction, operationId, cancellationToken);
            }

            var session = await ReadSessionAsync(connection, transaction, sessionId, cancellationToken)
                ?? throw new SnookException(SnookErrorCode.NotFound, "Session was not found.");
            if (session.State != SessionState.RecoveryRequired)
            {
                throw new SnookException(SnookErrorCode.InvalidTransition, "Only a recovery-required session can be resolved.");
            }

            if (session.Revision != expectedRevision)
            {
                throw new SnookException(SnookErrorCode.RevisionConflict, "The recovery item changed in another view. Refresh and try again.");
            }

            var openInterval = session.Intervals.SingleOrDefault(interval => interval.EndedAtUtc is null);
            if (decision == RecoveryDecision.StopNow && nowUtc < session.StartedAtUtc)
            {
                throw new SnookException(SnookErrorCode.ValidationFailed, "The current clock is before this session started; choose stop-at-last-known or continue.");
            }

            var nextRevision = session.Revision + 1;
            var nextState = decision == RecoveryDecision.Continue ? SessionState.Running : SessionState.Stopped;
            DateTimeOffset? stoppedAt = null;
            if (decision != RecoveryDecision.Continue)
            {
                stoppedAt = decision == RecoveryDecision.StopNow
                    ? nowUtc
                    : Max(session.StartedAtUtc, Min(nowUtc, session.UpdatedAtUtc));
                if (openInterval is not null)
                {
                    await CloseIntervalAtAsync(connection, transaction, openInterval.Id, Max(openInterval.StartedAtUtc, stoppedAt.Value), cancellationToken);
                }
            }
            else if (openInterval is null)
            {
                await InsertIntervalAsync(connection, transaction, sessionId, new TimeInterval(Guid.NewGuid(), nowUtc, null, "recovery-continue"), cancellationToken);
            }
            else if (openInterval.StartedAtUtc > nowUtc)
            {
                throw new SnookException(SnookErrorCode.ValidationFailed, "The current clock is before the active interval; choose stop-at-last-known or stop-now after correcting the clock.");
            }

            var decisionName = decision switch
            {
                RecoveryDecision.StopAtLastKnown => "stop-at-last-known",
                RecoveryDecision.StopNow => "stop-now",
                RecoveryDecision.Continue => "continue",
                _ => throw new ArgumentOutOfRangeException(nameof(decision))
            };
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE tracking_sessions SET state=$state, stopped_at_utc_ms=$stopped,
                    notes=CASE WHEN notes='' THEN $decision_note ELSE notes || char(10) || $decision_note END,
                    recovery_status=$recovery_status, updated_at_utc_ms=$updated, revision=$revision
                WHERE id=$id AND revision=$expected;
                """;
            command.Parameters.AddWithValue("$state", State(nextState));
            command.Parameters.AddWithValue("$stopped", stoppedAt is null ? DBNull.Value : stoppedAt.Value.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$decision_note", $"Recovery decision: {decisionName}.");
            command.Parameters.AddWithValue("$recovery_status", decisionName);
            command.Parameters.AddWithValue("$updated", nowUtc.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$revision", nextRevision);
            command.Parameters.AddWithValue("$id", Id(sessionId));
            command.Parameters.AddWithValue("$expected", expectedRevision);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new SnookException(SnookErrorCode.RevisionConflict, "The recovery item changed in another view. Refresh and try again.");
            }

            var updated = await ReadSessionAsync(connection, transaction, sessionId, cancellationToken)
                ?? throw new SnookException(SnookErrorCode.InternalError, "Session disappeared during recovery.");
            await RecordMutationAsync(connection, transaction, operationId, "session", sessionId, $"recovery-{decisionName}", session.Revision, nextRevision, nowUtc, cancellationToken);
            await RecordReceiptAsync(connection, transaction, operationId, sessionId, nextRevision, cancellationToken);
            return updated;
        }, cancellationToken);
    }

    public async Task<DomainCalendar> CreateCalendarAsync(string name, string color, bool visible, Guid operationId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        var validatedName = Guard.Required(name, "name", 200);
        var validatedColor = ValidateColor(color);
        return await WriteAsync(async (connection, transaction) =>
        {
            if (await ReceiptExistsAsync(connection, transaction, operationId, cancellationToken))
            {
                return await ReadCalendarByOperationAsync(connection, transaction, operationId, cancellationToken);
            }

            var workspaceId = await ReadWorkspaceIdAsync(connection, transaction, cancellationToken);
            await EnsureNameAvailableAsync(connection, transaction, "calendars", "workspace_id", workspaceId, validatedName, null, cancellationToken);
            var calendar = new DomainCalendar(Guid.NewGuid(), workspaceId, validatedName, validatedColor, visible, 1);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO calendars(id,workspace_id,name,color,visible,revision) VALUES($id,$workspace,$name,$color,$visible,1);";
            command.Parameters.AddWithValue("$id", Id(calendar.Id));
            command.Parameters.AddWithValue("$workspace", Id(workspaceId));
            command.Parameters.AddWithValue("$name", calendar.Name);
            command.Parameters.AddWithValue("$color", calendar.Color);
            command.Parameters.AddWithValue("$visible", calendar.Visible ? 1 : 0);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await RecordMutationAsync(connection, transaction, operationId, "calendar", calendar.Id, "created", 0, 1, nowUtc, cancellationToken);
            await RecordReceiptAsync(connection, transaction, operationId, calendar.Id, 1, cancellationToken);
            return calendar;
        }, cancellationToken);
    }

    public async Task<DomainCalendar> UpdateCalendarAsync(Guid calendarId, CalendarUpdate update, long expectedRevision, Guid operationId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        var validatedName = Guard.Required(update.Name, "name", 200);
        var validatedColor = ValidateColor(update.Color);
        return await WriteAsync(async (connection, transaction) =>
        {
            if (await ReceiptExistsAsync(connection, transaction, operationId, cancellationToken))
            {
                return await ReadCalendarByOperationAsync(connection, transaction, operationId, cancellationToken);
            }

            var calendar = await ReadCalendarAsync(connection, transaction, calendarId, false, cancellationToken)
                ?? throw new SnookException(SnookErrorCode.NotFound, "Calendar was not found.");
            if (calendar.Revision != expectedRevision)
            {
                throw new SnookException(SnookErrorCode.RevisionConflict, "The calendar changed in another view. Refresh and try again.");
            }

            await EnsureNameAvailableAsync(connection, transaction, "calendars", "workspace_id", calendar.WorkspaceId, validatedName, calendarId, cancellationToken);
            var nextRevision = calendar.Revision + 1;
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE calendars SET name=$name,color=$color,visible=$visible,revision=$revision WHERE id=$id AND revision=$expected AND deleted_at_utc_ms IS NULL;";
            command.Parameters.AddWithValue("$name", validatedName);
            command.Parameters.AddWithValue("$color", validatedColor);
            command.Parameters.AddWithValue("$visible", update.Visible ? 1 : 0);
            command.Parameters.AddWithValue("$revision", nextRevision);
            command.Parameters.AddWithValue("$id", Id(calendarId));
            command.Parameters.AddWithValue("$expected", expectedRevision);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new SnookException(SnookErrorCode.RevisionConflict, "The calendar changed in another view. Refresh and try again.");
            }

            var updated = calendar with { Name = validatedName, Color = validatedColor, Visible = update.Visible, Revision = nextRevision };
            await RecordMutationAsync(connection, transaction, operationId, "calendar", calendarId, "updated", calendar.Revision, nextRevision, nowUtc, cancellationToken);
            await RecordReceiptAsync(connection, transaction, operationId, calendarId, nextRevision, cancellationToken);
            return updated;
        }, cancellationToken);
    }

    public Task<DomainCalendar> SetCalendarDeletedAsync(Guid calendarId, bool deleted, long expectedRevision, Guid operationId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
        => SetCalendarDeletedCoreAsync(calendarId, deleted, expectedRevision, operationId, nowUtc, cancellationToken);

    private async Task<DomainCalendar> SetCalendarDeletedCoreAsync(Guid calendarId, bool deleted, long expectedRevision, Guid operationId, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        EnsureInitialized();
        return await WriteAsync(async (connection, transaction) =>
        {
            if (await ReceiptExistsAsync(connection, transaction, operationId, cancellationToken))
            {
                return await ReadCalendarByOperationAsync(connection, transaction, operationId, cancellationToken);
            }

            var calendar = await ReadCalendarAsync(connection, transaction, calendarId, true, cancellationToken)
                ?? throw new SnookException(SnookErrorCode.NotFound, "Calendar was not found.");
            if (calendar.Revision != expectedRevision)
            {
                throw new SnookException(SnookErrorCode.RevisionConflict, "The calendar changed in another view. Refresh and try again.");
            }

            if (!deleted)
            {
                await EnsureNameAvailableAsync(connection, transaction, "calendars", "workspace_id", calendar.WorkspaceId, calendar.Name, calendarId, cancellationToken);
            }

            var nextRevision = calendar.Revision + 1;
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE calendars SET deleted_at_utc_ms=$deleted,revision=$revision WHERE id=$id AND revision=$expected;";
            command.Parameters.AddWithValue("$deleted", deleted ? nowUtc.ToUnixTimeMilliseconds() : DBNull.Value);
            command.Parameters.AddWithValue("$revision", nextRevision);
            command.Parameters.AddWithValue("$id", Id(calendarId));
            command.Parameters.AddWithValue("$expected", expectedRevision);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new SnookException(SnookErrorCode.RevisionConflict, "The calendar changed in another view. Refresh and try again.");
            }

            var updated = calendar with { DeletedAtUtc = deleted ? (calendar.DeletedAtUtc ?? nowUtc) : null, Revision = nextRevision };
            await RecordMutationAsync(connection, transaction, operationId, "calendar", calendarId, deleted ? "deleted" : "restored-deleted", calendar.Revision, nextRevision, nowUtc, cancellationToken);
            await RecordReceiptAsync(connection, transaction, operationId, calendarId, nextRevision, cancellationToken);
            return updated;
        }, cancellationToken);
    }

    public async Task<CalendarEvent> CreateCalendarEventAsync(
        Guid calendarId,
        string title,
        DateTimeOffset startAtUtc,
        DateTimeOffset endAtUtc,
        string description,
        string? location,
        string color,
        bool allDay,
        string timeZone,
        string? recurrenceRule,
        DateTimeOffset? recurrenceEndUtc,
        Guid operationId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        var validated = ValidateCalendarEvent(title, description, location, color, startAtUtc, endAtUtc, timeZone, recurrenceRule, recurrenceEndUtc);
        return await WriteAsync(async (connection, transaction) =>
        {
            if (await ReceiptExistsAsync(connection, transaction, operationId, cancellationToken))
            {
                return await ReadCalendarEventByOperationAsync(connection, transaction, operationId, cancellationToken);
            }

            await EnsureCalendarAsync(connection, transaction, calendarId, cancellationToken);
            var item = new CalendarEvent(Guid.NewGuid(), calendarId, validated.Title, validated.Description, validated.Location, validated.Color, startAtUtc, endAtUtc, allDay, validated.TimeZone, validated.RecurrenceRule, recurrenceEndUtc, null, 1);
            await InsertCalendarEventAsync(connection, transaction, item, nowUtc, cancellationToken);
            await RecordMutationAsync(connection, transaction, operationId, "calendar-event", item.Id, "created", 0, 1, nowUtc, cancellationToken);
            await RecordReceiptAsync(connection, transaction, operationId, item.Id, 1, cancellationToken);
            return item;
        }, cancellationToken);
    }

    public async Task<CalendarEvent> UpdateCalendarEventAsync(Guid eventId, CalendarEventUpdate update, long expectedRevision, Guid operationId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        var validated = ValidateCalendarEvent(update.Title, update.Description, update.Location, update.Color, update.StartAtUtc, update.EndAtUtc, update.TimeZone, update.RecurrenceRule, update.RecurrenceEndUtc);
        return await WriteAsync(async (connection, transaction) =>
        {
            if (await ReceiptExistsAsync(connection, transaction, operationId, cancellationToken))
            {
                return await ReadCalendarEventByOperationAsync(connection, transaction, operationId, cancellationToken);
            }

            var before = await ReadCalendarEventAsync(connection, transaction, eventId, false, cancellationToken)
                ?? throw new SnookException(SnookErrorCode.NotFound, "Calendar event was not found.");
            if (before.Revision != expectedRevision)
            {
                throw new SnookException(SnookErrorCode.RevisionConflict, "The calendar event changed in another view. Refresh and try again.");
            }

            var nextRevision = before.Revision + 1;
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE calendar_events SET title=$title,description=$description,location=$location,color=$color,
                    start_at_utc_ms=$start,end_at_utc_ms=$end,all_day=$all_day,time_zone=$zone,
                    recurrence_rule=$rule,recurrence_end_utc_ms=$recurrence_end,revision=$revision,updated_at_utc_ms=$updated
                WHERE id=$id AND revision=$expected AND deleted_at_utc_ms IS NULL;
                """;
            AddCalendarEventParameters(command, update.StartAtUtc, update.EndAtUtc, update.AllDay, validated, update.RecurrenceEndUtc, nextRevision, eventId, expectedRevision, nowUtc);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new SnookException(SnookErrorCode.RevisionConflict, "The calendar event changed in another view. Refresh and try again.");
            }

            var updated = before with
            {
                Title = validated.Title,
                Description = validated.Description,
                Location = validated.Location,
                Color = validated.Color,
                StartAtUtc = update.StartAtUtc,
                EndAtUtc = update.EndAtUtc,
                AllDay = update.AllDay,
                TimeZone = validated.TimeZone,
                RecurrenceRule = validated.RecurrenceRule,
                RecurrenceEndUtc = update.RecurrenceEndUtc,
                Revision = nextRevision
            };
            await RecordMutationAsync(connection, transaction, operationId, "calendar-event", eventId, "updated", before.Revision, nextRevision, nowUtc, cancellationToken);
            await RecordReceiptAsync(connection, transaction, operationId, eventId, nextRevision, cancellationToken);
            return updated;
        }, cancellationToken);
    }

    public Task<CalendarEvent> SetCalendarEventDeletedAsync(Guid eventId, bool deleted, long expectedRevision, Guid operationId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
        => SetCalendarEventDeletedCoreAsync(eventId, deleted, expectedRevision, operationId, nowUtc, cancellationToken);

    private async Task<CalendarEvent> SetCalendarEventDeletedCoreAsync(Guid eventId, bool deleted, long expectedRevision, Guid operationId, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        EnsureInitialized();
        return await WriteAsync(async (connection, transaction) =>
        {
            if (await ReceiptExistsAsync(connection, transaction, operationId, cancellationToken))
            {
                return await ReadCalendarEventByOperationAsync(connection, transaction, operationId, cancellationToken);
            }

            var before = await ReadCalendarEventAsync(connection, transaction, eventId, true, cancellationToken)
                ?? throw new SnookException(SnookErrorCode.NotFound, "Calendar event was not found.");
            if (before.Revision != expectedRevision)
            {
                throw new SnookException(SnookErrorCode.RevisionConflict, "The calendar event changed in another view. Refresh and try again.");
            }

            var nextRevision = before.Revision + 1;
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE calendar_events SET deleted_at_utc_ms=$deleted,revision=$revision,updated_at_utc_ms=$updated WHERE id=$id AND revision=$expected;";
            command.Parameters.AddWithValue("$deleted", deleted ? nowUtc.ToUnixTimeMilliseconds() : DBNull.Value);
            command.Parameters.AddWithValue("$revision", nextRevision);
            command.Parameters.AddWithValue("$updated", nowUtc.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$id", Id(eventId));
            command.Parameters.AddWithValue("$expected", expectedRevision);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new SnookException(SnookErrorCode.RevisionConflict, "The calendar event changed in another view. Refresh and try again.");
            }

            var updated = before with { DeletedAtUtc = deleted ? (before.DeletedAtUtc ?? nowUtc) : null, Revision = nextRevision };
            await RecordMutationAsync(connection, transaction, operationId, "calendar-event", eventId, deleted ? "deleted" : "restored-deleted", before.Revision, nextRevision, nowUtc, cancellationToken);
            await RecordReceiptAsync(connection, transaction, operationId, eventId, nextRevision, cancellationToken);
            return updated;
        }, cancellationToken);
    }

    public async Task<CalendarEventOccurrenceOverride> UpsertCalendarEventExceptionAsync(Guid eventId, DateTimeOffset originalStartAtUtc, DateTimeOffset? newStartAtUtc, DateTimeOffset? newEndAtUtc, string? titleOverride, bool cancelled, long? expectedRevision, Guid operationId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        if (!cancelled && ((newStartAtUtc is null) != (newEndAtUtc is null) || (newStartAtUtc is not null && newEndAtUtc <= newStartAtUtc)))
        {
            throw new SnookException(SnookErrorCode.ValidationFailed, "A moved occurrence needs a valid new start and end.");
        }

        return await WriteAsync(async (connection, transaction) =>
        {
            if (await ReceiptExistsAsync(connection, transaction, operationId, cancellationToken))
            {
                return await ReadCalendarEventExceptionByOperationAsync(connection, transaction, operationId, cancellationToken);
            }

            var calendarEvent = await ReadCalendarEventAsync(connection, transaction, eventId, false, cancellationToken)
                ?? throw new SnookException(SnookErrorCode.NotFound, "Calendar event was not found.");
            if (calendarEvent.RecurrenceRule is null)
            {
                throw new SnookException(SnookErrorCode.ValidationFailed, "Occurrence exceptions require a recurring calendar event.");
            }

            var existing = await ReadCalendarEventExceptionAsync(connection, transaction, eventId, originalStartAtUtc, cancellationToken);
            if (existing is not null)
            {
                if (expectedRevision is null || existing.Revision != expectedRevision.Value)
                {
                    throw new SnookException(SnookErrorCode.RevisionConflict, "The calendar exception changed in another view. Refresh and try again.");
                }

                var nextRevision = existing.Revision + 1;
                await using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE calendar_event_exceptions SET new_start_at_utc_ms=$new_start,new_end_at_utc_ms=$new_end,title_override=$title,cancelled=$cancelled,revision=$revision WHERE id=$id AND revision=$expected;";
                AddExceptionParameters(update, newStartAtUtc, newEndAtUtc, titleOverride, cancelled, nextRevision, existing.Id, expectedRevision.Value);
                if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                {
                    throw new SnookException(SnookErrorCode.RevisionConflict, "The calendar exception changed in another view. Refresh and try again.");
                }

                var updated = existing with { NewStartAtUtc = newStartAtUtc, NewEndAtUtc = newEndAtUtc, TitleOverride = titleOverride, Cancelled = cancelled, Revision = nextRevision };
                await RecordMutationAsync(connection, transaction, operationId, "calendar-event-exception", existing.Id, "updated", existing.Revision, nextRevision, nowUtc, cancellationToken);
                await RecordReceiptAsync(connection, transaction, operationId, existing.Id, nextRevision, cancellationToken);
                return updated;
            }

            if (expectedRevision is not null)
            {
                throw new SnookException(SnookErrorCode.RevisionConflict, "The calendar exception does not exist; refresh and try again.");
            }

            var item = new CalendarEventOccurrenceOverride(Guid.NewGuid(), eventId, originalStartAtUtc, newStartAtUtc, newEndAtUtc, string.IsNullOrWhiteSpace(titleOverride) ? null : Guard.Optional(titleOverride, "titleOverride", 300), cancelled, 1);
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO calendar_event_exceptions(id,event_id,original_start_at_utc_ms,new_start_at_utc_ms,new_end_at_utc_ms,title_override,cancelled,revision) VALUES($id,$event,$original,$new_start,$new_end,$title,$cancelled,1);";
            insert.Parameters.AddWithValue("$id", Id(item.Id));
            insert.Parameters.AddWithValue("$event", Id(item.EventId));
            insert.Parameters.AddWithValue("$original", item.OriginalStartAtUtc.ToUnixTimeMilliseconds());
            insert.Parameters.AddWithValue("$new_start", item.NewStartAtUtc is null ? DBNull.Value : item.NewStartAtUtc.Value.ToUnixTimeMilliseconds());
            insert.Parameters.AddWithValue("$new_end", item.NewEndAtUtc is null ? DBNull.Value : item.NewEndAtUtc.Value.ToUnixTimeMilliseconds());
            insert.Parameters.AddWithValue("$title", item.TitleOverride is null ? DBNull.Value : item.TitleOverride);
            insert.Parameters.AddWithValue("$cancelled", item.Cancelled ? 1 : 0);
            await insert.ExecuteNonQueryAsync(cancellationToken);
            await RecordMutationAsync(connection, transaction, operationId, "calendar-event-exception", item.Id, "created", 0, 1, nowUtc, cancellationToken);
            await RecordReceiptAsync(connection, transaction, operationId, item.Id, 1, cancellationToken);
            return item;
        }, cancellationToken);
    }

    public async Task DeleteCalendarEventExceptionAsync(Guid exceptionId, long expectedRevision, Guid operationId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await WriteAsync(async (connection, transaction) =>
        {
            if (await ReceiptExistsAsync(connection, transaction, operationId, cancellationToken))
            {
                return true;
            }

            var exception = await ReadCalendarEventExceptionAsync(connection, transaction, exceptionId, cancellationToken)
                ?? throw new SnookException(SnookErrorCode.NotFound, "Calendar event exception was not found.");
            if (exception.Revision != expectedRevision)
            {
                throw new SnookException(SnookErrorCode.RevisionConflict, "The calendar exception changed in another view. Refresh and try again.");
            }

            await using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM calendar_event_exceptions WHERE id=$id AND revision=$expected;";
            delete.Parameters.AddWithValue("$id", Id(exceptionId));
            delete.Parameters.AddWithValue("$expected", expectedRevision);
            if (await delete.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new SnookException(SnookErrorCode.RevisionConflict, "The calendar exception changed in another view. Refresh and try again.");
            }

            await RecordMutationAsync(connection, transaction, operationId, "calendar-event-exception", exceptionId, "deleted", exception.Revision, exception.Revision + 1, nowUtc, cancellationToken);
            await RecordReceiptAsync(connection, transaction, operationId, exceptionId, exception.Revision + 1, cancellationToken);
            return true;
        }, cancellationToken);
    }

    public async Task<ScheduleBlock> CreateScheduleBlockAsync(
        Guid calendarId,
        Guid? taskId,
        Guid? activityId,
        string? titleOverride,
        DateTimeOffset startAtUtc,
        DateTimeOffset endAtUtc,
        string timeZone,
        string? recurrenceRule,
        DateTimeOffset? recurrenceEndUtc,
        Guid operationId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        if (endAtUtc <= startAtUtc)
        {
            throw new SnookException(SnookErrorCode.ValidationFailed, "A schedule block must end after it starts.");
        }

        if (taskId is null && activityId is null)
        {
            throw new SnookException(SnookErrorCode.ValidationFailed, "A schedule block needs a task or activity target.");
        }

        ValidateRecurrence(recurrenceRule, recurrenceEndUtc, startAtUtc);

        return await WriteAsync(async (connection, transaction) =>
        {
            if (await ReceiptExistsAsync(connection, transaction, operationId, cancellationToken))
            {
                return await ReadScheduleBlockByOperationAsync(connection, transaction, operationId, cancellationToken);
            }

            await EnsureCalendarAsync(connection, transaction, calendarId, cancellationToken);
            if (taskId is not null)
            {
                await EnsureTaskAsync(connection, transaction, taskId.Value, cancellationToken);
            }

            if (activityId is not null)
            {
                await EnsureActivityAsync(connection, transaction, activityId.Value, cancellationToken);
            }

            var block = new ScheduleBlock(
                Guid.NewGuid(),
                calendarId,
                taskId,
                activityId,
                string.IsNullOrWhiteSpace(titleOverride) ? null : Guard.Optional(titleOverride, "titleOverride", 300),
                startAtUtc,
                endAtUtc,
                Guard.Required(timeZone, "timeZone", 100),
                string.IsNullOrWhiteSpace(recurrenceRule) ? null : Guard.Required(recurrenceRule, "recurrenceRule", 1_000),
                recurrenceEndUtc,
                null,
                1);
            await InsertScheduleBlockAsync(connection, transaction, block, nowUtc, cancellationToken);
            var stored = await ReadScheduleBlockAsync(connection, transaction, block.Id, cancellationToken)
                ?? throw new SnookException(SnookErrorCode.InternalError, "Schedule block disappeared after creation.");
            await RecordMutationAsync(connection, transaction, operationId, "schedule-block", stored.Id, "created", 0, 1, nowUtc, cancellationToken);
            await RecordReceiptAsync(connection, transaction, operationId, stored.Id, 1, cancellationToken);
            return stored;
        }, cancellationToken);
    }

    public async Task<ScheduleBlock> UpdateScheduleBlockAsync(Guid blockId, ScheduleBlockUpdate update, long expectedRevision, Guid operationId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        if (update.EndAtUtc <= update.StartAtUtc)
        {
            throw new SnookException(SnookErrorCode.ValidationFailed, "A schedule block must end after it starts.");
        }

        ValidateRecurrence(update.RecurrenceRule, update.RecurrenceEndUtc, update.StartAtUtc);
        return await WriteAsync(async (connection, transaction) =>
        {
            if (await ReceiptExistsAsync(connection, transaction, operationId, cancellationToken))
            {
                return await ReadScheduleBlockByOperationAsync(connection, transaction, operationId, cancellationToken);
            }

            var block = await ReadScheduleBlockAsync(connection, transaction, blockId, cancellationToken)
                ?? throw new SnookException(SnookErrorCode.NotFound, "Schedule block was not found.");
            if (block.Revision != expectedRevision)
            {
                throw new SnookException(SnookErrorCode.RevisionConflict, "The schedule block changed in another view. Refresh and try again.");
            }

            var nextRevision = block.Revision + 1;
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE schedule_blocks SET title_override=$title, start_at_utc_ms=$start,
                    end_at_utc_ms=$end, time_zone=$zone, recurrence_rule=$rule,
                    recurrence_end_utc_ms=$recurrence_end, revision=$revision, updated_at_utc_ms=$updated
                WHERE id=$id AND revision=$expected AND deleted_at_utc_ms IS NULL;
                """;
            command.Parameters.AddWithValue("$title", string.IsNullOrWhiteSpace(update.TitleOverride) ? DBNull.Value : Guard.Optional(update.TitleOverride, "titleOverride", 300));
            command.Parameters.AddWithValue("$start", update.StartAtUtc.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$end", update.EndAtUtc.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$zone", Guard.Required(update.TimeZone, "timeZone", 100));
            command.Parameters.AddWithValue("$rule", string.IsNullOrWhiteSpace(update.RecurrenceRule) ? DBNull.Value : update.RecurrenceRule);
            command.Parameters.AddWithValue("$recurrence_end", update.RecurrenceEndUtc is null ? DBNull.Value : update.RecurrenceEndUtc.Value.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$revision", nextRevision);
            command.Parameters.AddWithValue("$updated", nowUtc.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$id", Id(blockId));
            command.Parameters.AddWithValue("$expected", expectedRevision);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new SnookException(SnookErrorCode.RevisionConflict, "The schedule block changed in another view. Refresh and try again.");
            }

            var updated = block with
            {
                StartAtUtc = update.StartAtUtc,
                EndAtUtc = update.EndAtUtc,
                TimeZone = update.TimeZone,
                TitleOverride = string.IsNullOrWhiteSpace(update.TitleOverride) ? null : update.TitleOverride,
                RecurrenceRule = string.IsNullOrWhiteSpace(update.RecurrenceRule) ? null : update.RecurrenceRule,
                RecurrenceEndUtc = update.RecurrenceEndUtc,
                Revision = nextRevision
            };
            await RecordMutationAsync(connection, transaction, operationId, "schedule-block", blockId, "updated", block.Revision, nextRevision, nowUtc, cancellationToken);
            await RecordReceiptAsync(connection, transaction, operationId, blockId, nextRevision, cancellationToken);
            return updated;
        }, cancellationToken);
    }

    public async Task<TrackingSession> CreateManualSessionAsync(
        Guid? taskId,
        Guid? activityId,
        DateTimeOffset startedAtUtc,
        DateTimeOffset endedAtUtc,
        string notes,
        Guid operationId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        if (taskId is null && activityId is null)
        {
            throw new SnookException(SnookErrorCode.ValidationFailed, "A session needs a task or activity target.");
        }

        if (endedAtUtc <= startedAtUtc)
        {
            throw new SnookException(SnookErrorCode.ValidationFailed, "Manual time must end after it starts.");
        }

        return await WriteAsync(async (connection, transaction) =>
        {
            if (await ReceiptExistsAsync(connection, transaction, operationId, cancellationToken))
            {
                return await ReadSessionByOperationAsync(connection, transaction, operationId, cancellationToken);
            }

            if (taskId is not null)
            {
                var task = await ReadTaskAsync(connection, transaction, taskId.Value, cancellationToken)
                    ?? throw new SnookException(SnookErrorCode.NotFound, "Task was not found.");
                if (task.ArchivedAtUtc is not null)
                {
                    throw new SnookException(SnookErrorCode.InvalidTransition, "Archived tasks cannot receive new time.");
                }
            }

            if (activityId is not null)
            {
                await EnsureActivityAsync(connection, transaction, activityId.Value, cancellationToken);
            }

            var session = new TrackingSession(
                Guid.NewGuid(),
                await ReadWorkspaceIdAsync(connection, transaction, cancellationToken),
                taskId,
                activityId,
                SessionLane.Foreground,
                SessionState.Stopped,
                startedAtUtc,
                endedAtUtc,
                Guard.Optional(notes, "notes"),
                nowUtc,
                nowUtc,
                1,
                [new TimeInterval(Guid.NewGuid(), startedAtUtc, endedAtUtc, "manual")]);
            await InsertSessionAsync(connection, transaction, session, cancellationToken);
            await RecordMutationAsync(connection, transaction, operationId, "session", session.Id, "manual", 0, 1, nowUtc, cancellationToken);
            await RecordReceiptAsync(connection, transaction, operationId, session.Id, 1, cancellationToken);
            return session;
        }, cancellationToken);
    }

    public async Task<TrackingSession> CorrectSessionAsync(Guid sessionId, SessionCorrection correction, long expectedRevision, Guid operationId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        var reason = Guard.Required(correction.Reason, "reason", 1_000);
        var notes = string.IsNullOrWhiteSpace(correction.Notes) ? string.Empty : Guard.Optional(correction.Notes, "notes");
        ValidateCorrection(correction);

        return await WriteAsync(async (connection, transaction) =>
        {
            if (await ReceiptExistsAsync(connection, transaction, operationId, cancellationToken))
            {
                return await ReadSessionByOperationAsync(connection, transaction, operationId, cancellationToken);
            }

            var before = await ReadSessionAsync(connection, transaction, sessionId, cancellationToken)
                ?? throw new SnookException(SnookErrorCode.NotFound, "Session was not found.");
            if (before.State == SessionState.RecoveryRequired)
            {
                throw new SnookException(SnookErrorCode.InvalidTransition, "Resolve the recovery-required session before editing it.");
            }

            var correctedOpenCount = correction.Intervals.Count(interval => interval.EndedAtUtc is null);
            if (before.State == SessionState.Running && (correctedOpenCount != 1 || correction.StoppedAtUtc is not null))
            {
                throw new SnookException(SnookErrorCode.ValidationFailed, "A running session needs exactly one open interval and no stop time.");
            }

            if ((before.State is SessionState.Paused or SessionState.Stopped) && correctedOpenCount != 0)
            {
                throw new SnookException(SnookErrorCode.ValidationFailed, "Paused and stopped sessions cannot have an open interval.");
            }

            if (before.State == SessionState.Stopped && correction.StoppedAtUtc is null)
            {
                throw new SnookException(SnookErrorCode.ValidationFailed, "A stopped session needs a stop time.");
            }

            if (before.Revision != expectedRevision)
            {
                throw new SnookException(SnookErrorCode.RevisionConflict, "The session changed in another view. Refresh and try again.");
            }

            if (correction.TaskId is not null)
            {
                var task = await ReadTaskAsync(connection, transaction, correction.TaskId.Value, cancellationToken)
                    ?? throw new SnookException(SnookErrorCode.NotFound, "Task was not found.");
                if (task.ArchivedAtUtc is not null)
                {
                    throw new SnookException(SnookErrorCode.InvalidTransition, "Archived tasks cannot receive corrected time.");
                }
            }

            if (correction.ActivityId is not null)
            {
                await EnsureActivityAsync(connection, transaction, correction.ActivityId.Value, cancellationToken);
            }

            var nextRevision = before.Revision + 1;
            var after = before with
            {
                TaskId = correction.TaskId,
                ActivityId = correction.ActivityId,
                StartedAtUtc = correction.StartedAtUtc,
                StoppedAtUtc = correction.StoppedAtUtc,
                Notes = notes,
                UpdatedAtUtc = nowUtc,
                Revision = nextRevision,
                Intervals = correction.Intervals.ToArray()
            };

            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE tracking_sessions SET task_id=$task, activity_id=$activity,
                    started_at_utc_ms=$started, stopped_at_utc_ms=$stopped, notes=$notes,
                    updated_at_utc_ms=$updated, revision=$revision
                WHERE id=$id AND revision=$expected;
                """;
            update.Parameters.AddWithValue("$task", after.TaskId is null ? DBNull.Value : Id(after.TaskId.Value));
            update.Parameters.AddWithValue("$activity", after.ActivityId is null ? DBNull.Value : Id(after.ActivityId.Value));
            update.Parameters.AddWithValue("$started", after.StartedAtUtc.ToUnixTimeMilliseconds());
            update.Parameters.AddWithValue("$stopped", after.StoppedAtUtc is null ? DBNull.Value : after.StoppedAtUtc.Value.ToUnixTimeMilliseconds());
            update.Parameters.AddWithValue("$notes", after.Notes);
            update.Parameters.AddWithValue("$updated", nowUtc.ToUnixTimeMilliseconds());
            update.Parameters.AddWithValue("$revision", nextRevision);
            update.Parameters.AddWithValue("$id", Id(sessionId));
            update.Parameters.AddWithValue("$expected", expectedRevision);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new SnookException(SnookErrorCode.RevisionConflict, "The session changed in another view. Refresh and try again.");
            }

            await using var deleteIntervals = connection.CreateCommand();
            deleteIntervals.Transaction = transaction;
            deleteIntervals.CommandText = "DELETE FROM active_intervals WHERE session_id=$session;";
            deleteIntervals.Parameters.AddWithValue("$session", Id(sessionId));
            await deleteIntervals.ExecuteNonQueryAsync(cancellationToken);
            foreach (var interval in after.Intervals)
            {
                await InsertIntervalAsync(connection, transaction, sessionId, interval, cancellationToken);
            }

            await InsertTrackingCorrectionAsync(connection, transaction, new TrackingCorrection(Guid.NewGuid(), sessionId, operationId, reason, before, after, nowUtc), cancellationToken);
            await RecordMutationAsync(connection, transaction, operationId, "session", sessionId, "corrected", before.Revision, nextRevision, nowUtc, cancellationToken);
            await RecordReceiptAsync(connection, transaction, operationId, sessionId, nextRevision, cancellationToken);
            return after;
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<TrackingCorrection>> GetSessionCorrectionsAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var result = new List<TrackingCorrection>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,session_id,operation_id,reason,before_json,after_json,applied_at_utc_ms FROM tracking_corrections WHERE session_id=$session ORDER BY applied_at_utc_ms;";
        command.Parameters.AddWithValue("$session", Id(sessionId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var before = JsonSerializer.Deserialize<TrackingSession>(reader.GetString(4)) ?? throw new SnookException(SnookErrorCode.SchemaIncompatible, "A stored correction has invalid before-state data.");
            var after = JsonSerializer.Deserialize<TrackingSession>(reader.GetString(5)) ?? throw new SnookException(SnookErrorCode.SchemaIncompatible, "A stored correction has invalid after-state data.");
            result.Add(new TrackingCorrection(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), Guid.Parse(reader.GetString(2)), reader.GetString(3), before, after, FromMs(reader.GetInt64(6))));
        }

        return result;
    }

    public async Task<StoreBackupResult> CreateBackupAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var fullDestination = Path.GetFullPath(destinationPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullDestination) ?? Directory.GetCurrentDirectory());
            await using var source = await OpenConnectionAsync(cancellationToken);
            await using var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = fullDestination, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
            await destination.OpenAsync(cancellationToken);
            source.BackupDatabase(destination);
            await destination.CloseAsync();
            var bytes = new FileInfo(fullDestination).Length;
            var sha256 = await HashFileAsync(fullDestination, cancellationToken);
            var manifestPath = fullDestination + ".manifest.json";
            var manifest = new
            {
                formatVersion = 1,
                createdAtUtc = DateTimeOffset.UtcNow,
                databasePath = fullDestination,
                bytes,
                sha256,
                integrity = "sqlite-backup-verified"
            };
            await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, ExportJsonOptions), cancellationToken);
            return new StoreBackupResult(fullDestination, bytes, sha256, manifestPath);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<StoreExportResult> ExportJsonAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        var state = await LoadStateAsync(DateTimeOffset.UtcNow, cancellationToken);
        var corrections = new List<TrackingCorrection>();
        foreach (var session in state.Sessions)
        {
            corrections.AddRange(await GetSessionCorrectionsAsync(session.Id, cancellationToken));
        }

        var payload = new
        {
            schemaVersion = 4,
            exportedAtUtc = DateTimeOffset.UtcNow,
            workspace = state.Workspace,
            boards = state.Boards,
            projects = state.Projects,
            activityGroups = state.ActivityGroups,
            activities = state.Activities,
            calendars = state.Calendars,
            scheduleBlocks = state.ScheduleBlocks,
            calendarEvents = state.CalendarEvents,
            calendarEventExceptions = state.CalendarEventExceptions,
            tags = state.Tags,
            taskTagIds = state.TaskTagIds,
            projectTagIds = state.ProjectTagIds,
            activityTagIds = state.ActivityTagIds,
            tasks = state.Tasks,
            sessions = state.Sessions,
            corrections,
            habitTracking = await ExportHabitsAsync(cancellationToken),
            journaling = await ExportJournalsAsync(cancellationToken)
        };
        var fullDestination = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullDestination) ?? Directory.GetCurrentDirectory());
        await File.WriteAllTextAsync(fullDestination, JsonSerializer.Serialize(payload, ExportJsonOptions), cancellationToken);
        var bytes = new FileInfo(fullDestination).Length;
        return new StoreExportResult(fullDestination, bytes, await HashFileAsync(fullDestination, cancellationToken), 4);
    }

    public async Task<StoreExportResult> ExportCsvAsync(string destinationPath, DateTimeOffset rangeStartUtc, DateTimeOffset rangeEndUtc, CancellationToken cancellationToken = default)
    {
        if (rangeEndUtc <= rangeStartUtc)
        {
            throw new SnookException(SnookErrorCode.ValidationFailed, "CSV report range must end after it starts.");
        }

        var now = DateTimeOffset.UtcNow;
        var state = await LoadStateAsync(now, cancellationToken);
        var tasks = state.Tasks.ToDictionary(task => task.Id);
        var projects = state.Projects.ToDictionary(project => project.Id);
        var activities = state.Activities.ToDictionary(activity => activity.Id);
        var csv = new StringBuilder("session_id,interval_id,started_at_utc,ended_at_utc,task,project,activity,lane,state,attributed_milliseconds,notes\n");
        foreach (var session in state.Sessions)
        {
            var task = session.TaskId is not null ? tasks.GetValueOrDefault(session.TaskId.Value) : null;
            var project = task is not null ? projects.GetValueOrDefault(task.ProjectId) : null;
            var activity = session.ActivityId is not null ? activities.GetValueOrDefault(session.ActivityId.Value) : null;
            foreach (var interval in session.Intervals)
            {
                var milliseconds = TimeMath.OverlapMilliseconds(interval, rangeStartUtc, rangeEndUtc, now);
                if (milliseconds <= 0)
                {
                    continue;
                }

                var effectiveEnd = interval.EndedAtUtc ?? now;
                csv.Append(CsvField(session.Id.ToString("D"))).Append(',')
                    .Append(CsvField(interval.Id.ToString("D"))).Append(',')
                    .Append(CsvField(interval.StartedAtUtc.ToString("O", CultureInfo.InvariantCulture))).Append(',')
                    .Append(CsvField(effectiveEnd.ToString("O", CultureInfo.InvariantCulture))).Append(',')
                    .Append(CsvField(task?.Title)).Append(',')
                    .Append(CsvField(project?.Name)).Append(',')
                    .Append(CsvField(activity?.Name)).Append(',')
                    .Append(CsvField(session.Lane.ToString())).Append(',')
                    .Append(CsvField(session.State.ToString())).Append(',')
                    .Append(milliseconds.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(CsvField(session.Notes)).AppendLine();
            }
        }

        var fullDestination = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullDestination) ?? Directory.GetCurrentDirectory());
        await File.WriteAllTextAsync(fullDestination, csv.ToString(), cancellationToken);
        var bytes = new FileInfo(fullDestination).Length;
        return new StoreExportResult(fullDestination, bytes, await HashFileAsync(fullDestination, cancellationToken), 1);
    }

    public async Task<StoreBackupResult> RestoreBackupAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        var fullSource = Path.GetFullPath(sourcePath);
        if (!File.Exists(fullSource))
        {
            throw new SnookException(SnookErrorCode.NotFound, "The selected backup file was not found.");
        }

        await _writeGate.WaitAsync(cancellationToken);
        var stagingPath = _databasePath + $".restore-{Guid.NewGuid():N}";
        var stagingBasePath = stagingPath;
        var archivePath = _databasePath + $".before-restore-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}";
        var activeMoved = false;
        var walMoved = false;
        var shmMoved = false;
        try
        {
            await using (var source = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = fullSource,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                Pooling = false
            }.ToString()))
            {
                await source.OpenAsync(cancellationToken);
                await using var sourceCheck = source.CreateCommand();
                sourceCheck.CommandText = "PRAGMA quick_check;";
                var sourceResult = Convert.ToString(await sourceCheck.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
                if (!string.Equals(sourceResult, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    throw new SnookException(SnookErrorCode.SchemaIncompatible, "The selected backup failed SQLite integrity validation.");
                }

                await using var staging = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = stagingPath,
                    Mode = SqliteOpenMode.ReadWriteCreate,
                    Cache = SqliteCacheMode.Private,
                    Pooling = false
                }.ToString());
                await staging.OpenAsync(cancellationToken);
                source.BackupDatabase(staging);
            }

            await using (var validation = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = stagingPath,
                Mode = SqliteOpenMode.ReadWrite,
                Cache = SqliteCacheMode.Private,
                Pooling = false
            }.ToString()))
            {
                await validation.OpenAsync(cancellationToken);
                await using var pragma = validation.CreateCommand();
                pragma.CommandText = "PRAGMA foreign_keys = ON;";
                await pragma.ExecuteNonQueryAsync(cancellationToken);
                await ValidateIntegrityAsync(validation, cancellationToken);
                _ = await ReadWorkspaceAsync(validation, cancellationToken);
                // Upgrade the staging copy before replacing the live workspace. A v7
                // backup has no habit tables, and must be usable immediately after restore.
                await EnsureSchemaVersionAsync(validation, cancellationToken);
                await EnsureTaskWorkspaceMigrationAsync(validation, cancellationToken);
                await EnsureHabitsMigrationAsync(validation, cancellationToken);
                await EnsureJournalsMigrationAsync(validation, cancellationToken);
                await ValidateIntegrityAsync(validation, cancellationToken);
                await using var checkpoint = validation.CreateCommand();
                checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                await checkpoint.ExecuteNonQueryAsync(cancellationToken);
            }

            if (File.Exists(_databasePath))
            {
                File.Move(_databasePath, archivePath);
                activeMoved = true;
                walMoved = MoveIfExists(_databasePath + "-wal", archivePath + "-wal");
                shmMoved = MoveIfExists(_databasePath + "-shm", archivePath + "-shm");
            }

            File.Move(stagingPath, _databasePath);
            stagingPath = string.Empty;
            var restoredManifestPath = fullSource + ".manifest.json";
            return new StoreBackupResult(fullSource, new FileInfo(_databasePath).Length, await HashFileAsync(_databasePath, cancellationToken), File.Exists(restoredManifestPath) ? restoredManifestPath : null);
        }
        catch
        {
            if (activeMoved && !File.Exists(_databasePath) && File.Exists(archivePath))
            {
                File.Move(archivePath, _databasePath);
                if (walMoved)
                {
                    MoveIfExists(archivePath + "-wal", _databasePath + "-wal");
                }
                if (shmMoved)
                {
                    MoveIfExists(archivePath + "-shm", _databasePath + "-shm");
                }
            }

            throw;
        }
        finally
        {
            if (!string.IsNullOrEmpty(stagingPath) && File.Exists(stagingPath))
            {
                File.Delete(stagingPath);
            }
            DeleteIfExists(stagingBasePath + "-wal");
            DeleteIfExists(stagingBasePath + "-shm");

            _writeGate.Release();
        }
    }

    private async Task<T> WriteAsync<T>(Func<SqliteConnection, SqliteTransaction, Task<T>> action, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                var result = await action(connection, transaction);
                await transaction.CommitAsync(cancellationToken);
                return result;
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000; PRAGMA journal_mode = WAL; PRAGMA synchronous = FULL;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private static async Task SeedAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM workspaces;";
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) != 0)
        {
            return;
        }

        var workspaceId = Guid.NewGuid();
        var boardId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var focusId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var calendarId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteSeedAsync(connection, transaction, "INSERT INTO workspaces(id,name,created_at_utc_ms,revision) VALUES($id,$name,$now,1);", workspaceId, null, "My Workspace", "foreground", now, cancellationToken);
        await ExecuteSeedAsync(connection, transaction, "INSERT INTO boards(id,workspace_id,name,sort_key,revision) VALUES($id,$workspace,$name,1,1);", boardId, workspaceId, "My Work", "foreground", now, cancellationToken);
        await ExecuteSeedAsync(connection, transaction, "INSERT INTO projects(id,board_id,name,description,starred,sort_key,revision) VALUES($id,$board,$name,'',1,1,1);", projectId, boardId, "Inbox", "foreground", now, cancellationToken);
        await ExecuteSeedAsync(connection, transaction, "INSERT INTO activities(id,workspace_id,name,description,lane_default,revision) VALUES($id,$workspace,$name,'','foreground',1);", focusId, workspaceId, "Focus", "foreground", now, cancellationToken);
        await ExecuteSeedAsync(connection, transaction, "INSERT INTO activities(id,workspace_id,name,description,lane_default,revision) VALUES($id,$workspace,$name,'','background',1);", adminId, workspaceId, "Admin", "background", now, cancellationToken);
        await ExecuteSeedAsync(connection, transaction, "INSERT INTO calendars(id,workspace_id,name,color,visible,revision) VALUES($id,$workspace,$name,'#6767F2',1,1);", calendarId, workspaceId, "My calendar", "foreground", now, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task EnsureDefaultCalendarAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO calendars(id,workspace_id,name,color,visible,revision)
            SELECT $id, id, 'My calendar', '#6767F2', 1, 1 FROM workspaces
            WHERE NOT EXISTS (SELECT 1 FROM calendars);
            """;
        command.Parameters.AddWithValue("$id", Id(Guid.NewGuid()));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureDefaultSettingsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO workspace_settings(workspace_id,allow_concurrent_foreground,revision) SELECT id,0,1 FROM workspaces WHERE NOT EXISTS (SELECT 1 FROM workspace_settings);";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureSchemaVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.CommandText = "SELECT sequence,checksum FROM schema_migrations ORDER BY sequence DESC LIMIT 1;";
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            await reader.DisposeAsync();
            await using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO schema_migrations(sequence,name,checksum,applied_at_utc_ms) VALUES(1,'initial',$checksum,$applied);";
            insert.Parameters.AddWithValue("$checksum", SchemaChecksum);
            insert.Parameters.AddWithValue("$applied", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await insert.ExecuteNonQueryAsync(cancellationToken);
            return;
        }

        var sequence = reader.GetInt32(0);
        var current = reader.GetString(1);
        await reader.DisposeAsync();
        if (sequence > 9 || (sequence == 9 && current != JournalsChecksum) || (sequence == 8 && current != HabitsChecksum) || (sequence == 7 && current != TaskWorkspaceChecksum))
            throw new SnookException(SnookErrorCode.SchemaIncompatible, "The workspace schema is newer or incompatible with this Snook build.");
        if (sequence >= 7) return;
        if (string.Equals(current, SchemaChecksum, StringComparison.Ordinal))
        {
            return;
        }

        var hasRecoveryColumns = await HasColumnAsync(connection, "tracking_sessions", "recovery_status", cancellationToken);
        var hasCorrectionLedger = await HasTableAsync(connection, "tracking_corrections", cancellationToken);
        if ((sequence == 1 && !hasRecoveryColumns) || sequence == 2)
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                var nextSequence = sequence;
                if (!hasRecoveryColumns)
                {
                    await ExecuteMigrationCommandAsync(connection, transaction, "ALTER TABLE tracking_sessions ADD COLUMN recovery_status TEXT;", cancellationToken);
                    await ExecuteMigrationCommandAsync(connection, transaction, "ALTER TABLE tracking_sessions ADD COLUMN recovery_reason TEXT;", cancellationToken);
                    nextSequence = 2;
                    await InsertMigrationAsync(connection, transaction, nextSequence, "recovery-provenance", cancellationToken);
                }

                if (!hasCorrectionLedger)
                {
                    await ExecuteMigrationCommandAsync(connection, transaction, "CREATE TABLE tracking_corrections(id TEXT PRIMARY KEY, session_id TEXT NOT NULL REFERENCES tracking_sessions(id), operation_id TEXT NOT NULL UNIQUE, reason TEXT NOT NULL, before_json TEXT NOT NULL, after_json TEXT NOT NULL, applied_at_utc_ms INTEGER NOT NULL);", cancellationToken);
                }

                if (nextSequence < 3)
                {
                    await InsertMigrationAsync(connection, transaction, 3, "tracking-corrections", cancellationToken);
                }

                if (nextSequence < 4)
                {
                    await InsertMigrationAsync(connection, transaction, 4, "calendar-events", cancellationToken);
                }

                if (nextSequence < 5)
                {
                    await InsertMigrationAsync(connection, transaction, 5, "workspace-settings", cancellationToken);
                }

                if (nextSequence < 6)
                {
                    await ApplyActivityGroupsMigrationAsync(connection, transaction, cancellationToken);
                    await InsertMigrationAsync(connection, transaction, 6, "activity-groups", cancellationToken);
                }

                await transaction.CommitAsync(cancellationToken);
                return;
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
        }

        if (sequence == 3)
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                await InsertMigrationAsync(connection, transaction, 4, "calendar-events", cancellationToken);
                await InsertMigrationAsync(connection, transaction, 5, "workspace-settings", cancellationToken);
                await ApplyActivityGroupsMigrationAsync(connection, transaction, cancellationToken);
                await InsertMigrationAsync(connection, transaction, 6, "activity-groups", cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return;
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
        }

        if (sequence == 4)
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                await InsertMigrationAsync(connection, transaction, 5, "workspace-settings", cancellationToken);
                await ApplyActivityGroupsMigrationAsync(connection, transaction, cancellationToken);
                await InsertMigrationAsync(connection, transaction, 6, "activity-groups", cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return;
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
        }

        if (sequence == 5)
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                await ApplyActivityGroupsMigrationAsync(connection, transaction, cancellationToken);
                await InsertMigrationAsync(connection, transaction, 6, "activity-groups", cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return;
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
        }

        throw new SnookException(SnookErrorCode.SchemaIncompatible, "The workspace schema is newer or incompatible with this Snook build.");
    }

    private static async Task<bool> HasColumnAsync(SqliteConnection connection, string table, string column, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task ApplyActivityGroupsMigrationAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await ExecuteMigrationCommandAsync(connection, transaction, "CREATE TABLE IF NOT EXISTS activity_groups(id TEXT PRIMARY KEY, workspace_id TEXT NOT NULL REFERENCES workspaces(id), name TEXT NOT NULL, sort_key REAL NOT NULL, deleted_at_utc_ms INTEGER, revision INTEGER NOT NULL CHECK(revision > 0), UNIQUE(workspace_id,name COLLATE NOCASE));", cancellationToken);
        if (!await HasColumnAsync(connection, "activities", "group_id", cancellationToken))
        {
            await ExecuteMigrationCommandAsync(connection, transaction, "ALTER TABLE activities ADD COLUMN group_id TEXT REFERENCES activity_groups(id);", cancellationToken);
        }
    }

    private static async Task<bool> HasTableAsync(SqliteConnection connection, string table, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name);";
        command.Parameters.AddWithValue("$name", table);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1;
    }

    private static Task InsertMigrationAsync(SqliteConnection connection, SqliteTransaction transaction, int sequence, string name, CancellationToken cancellationToken)
        => ExecuteMigrationCommandAsync(connection, transaction, "INSERT INTO schema_migrations(sequence,name,checksum,applied_at_utc_ms) VALUES($sequence,$name,$checksum,$applied);", cancellationToken,
            ("$sequence", sequence), ("$name", name), ("$checksum", SchemaChecksum), ("$applied", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

    private static async Task ExecuteMigrationCommandAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ValidateIntegrityAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var quickCheck = connection.CreateCommand();
        quickCheck.CommandText = "PRAGMA quick_check;";
        var quickResult = Convert.ToString(await quickCheck.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        if (!string.Equals(quickResult, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new SnookException(SnookErrorCode.SchemaIncompatible, "The workspace failed SQLite integrity validation.");
        }

        await using var foreignKeys = connection.CreateCommand();
        foreignKeys.CommandText = "PRAGMA foreign_key_check;";
        await using var reader = await foreignKeys.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            throw new SnookException(SnookErrorCode.SchemaIncompatible, "The workspace contains an invalid relationship.");
        }
    }

    private static async Task DetectRecoveryRequiredAsync(SqliteConnection connection, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var candidates = new List<(Guid Id, long Revision, string Reason)>();
        await using (var query = connection.CreateCommand())
        {
            query.CommandText = """
                SELECT sessions.id, sessions.revision,
                    CASE
                        WHEN intervals.id IS NULL THEN 'missing-open-interval'
                        WHEN intervals.started_at_utc_ms > $now THEN 'clock-rollback'
                    END
                FROM tracking_sessions AS sessions
                LEFT JOIN active_intervals AS intervals
                    ON intervals.session_id = sessions.id AND intervals.ended_at_utc_ms IS NULL
                WHERE sessions.state = 'running' AND (intervals.id IS NULL OR intervals.started_at_utc_ms > $now);
                """;
            query.Parameters.AddWithValue("$now", nowUtc.ToUnixTimeMilliseconds());
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                candidates.Add((Guid.Parse(reader.GetString(0)), reader.GetInt64(1), reader.GetString(2)));
            }
        }

        if (candidates.Count == 0)
        {
            return;
        }

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var candidate in candidates)
            {
                var operationId = Guid.NewGuid();
                var nextRevision = candidate.Revision + 1;
                await using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE tracking_sessions SET state='recovery-required', recovery_status=$status, recovery_reason=$reason, updated_at_utc_ms=$updated, revision=$revision WHERE id=$id AND revision=$expected AND state='running';";
                update.Parameters.AddWithValue("$status", candidate.Reason);
                update.Parameters.AddWithValue("$reason", candidate.Reason == "clock-rollback" ? "The active interval begins after the host clock." : "The running session has no open interval.");
                update.Parameters.AddWithValue("$updated", nowUtc.ToUnixTimeMilliseconds());
                update.Parameters.AddWithValue("$revision", nextRevision);
                update.Parameters.AddWithValue("$id", Id(candidate.Id));
                update.Parameters.AddWithValue("$expected", candidate.Revision);
                if (await update.ExecuteNonQueryAsync(cancellationToken) == 1)
                {
                    await RecordMutationAsync(connection, transaction, operationId, "session", candidate.Id, "recovery-required", candidate.Revision, nextRevision, nowUtc, cancellationToken);
                    await RecordReceiptAsync(connection, transaction, operationId, candidate.Id, nextRevision, cancellationToken);
                }
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task ExecuteSeedAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, Guid id, Guid? parentId, string name, string lane, long now, CancellationToken cancellationToken)
    {
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = sql;
        insert.Parameters.AddWithValue("$id", Id(id));
        if (sql.Contains("$workspace", StringComparison.Ordinal))
        {
            insert.Parameters.AddWithValue("$workspace", Id(parentId ?? throw new InvalidOperationException("Seed parent is missing.")));
        }
        if (sql.Contains("$board", StringComparison.Ordinal))
        {
            insert.Parameters.AddWithValue("$board", Id(parentId ?? throw new InvalidOperationException("Seed parent is missing.")));
        }
        insert.Parameters.AddWithValue("$name", name);
        insert.Parameters.AddWithValue("$now", now);
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertTaskAsync(SqliteConnection connection, SqliteTransaction transaction, TaskItem task, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO tasks(id,project_id,title,description,priority,status,due_date,default_activity_id,starred,completed_at_utc_ms,revision,created_at_utc_ms,updated_at_utc_ms)
            VALUES($id,$project,$title,$description,$priority,$status,$due_date,$activity,$starred,$completed,$revision,$created,$updated);
            """;
        BindTask(command, task, nowUtc);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertSessionAsync(SqliteConnection connection, SqliteTransaction transaction, TrackingSession session, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO tracking_sessions(id,workspace_id,task_id,activity_id,lane,state,started_at_utc_ms,stopped_at_utc_ms,notes,recovery_status,recovery_reason,created_at_utc_ms,updated_at_utc_ms,revision)
            VALUES($id,$workspace,$task,$activity,$lane,$state,$started,$stopped,$notes,$recovery_status,$recovery_reason,$created,$updated,$revision);
            """;
        command.Parameters.AddWithValue("$id", Id(session.Id));
        command.Parameters.AddWithValue("$workspace", Id(session.WorkspaceId));
        command.Parameters.AddWithValue("$task", session.TaskId is null ? DBNull.Value : Id(session.TaskId.Value));
        command.Parameters.AddWithValue("$activity", session.ActivityId is null ? DBNull.Value : Id(session.ActivityId.Value));
        command.Parameters.AddWithValue("$lane", Lane(session.Lane));
        command.Parameters.AddWithValue("$state", State(session.State));
        command.Parameters.AddWithValue("$started", session.StartedAtUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$stopped", session.StoppedAtUtc is null ? DBNull.Value : session.StoppedAtUtc.Value.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$notes", session.Notes);
        command.Parameters.AddWithValue("$recovery_status", session.RecoveryStatus is null ? DBNull.Value : session.RecoveryStatus);
        command.Parameters.AddWithValue("$recovery_reason", session.RecoveryReason is null ? DBNull.Value : session.RecoveryReason);
        command.Parameters.AddWithValue("$created", session.CreatedAtUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$updated", session.UpdatedAtUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$revision", session.Revision);
        await command.ExecuteNonQueryAsync(cancellationToken);
        foreach (var interval in session.Intervals)
        {
            await InsertIntervalAsync(connection, transaction, session.Id, interval, cancellationToken);
        }
    }

    private static async Task InsertScheduleBlockAsync(SqliteConnection connection, SqliteTransaction transaction, ScheduleBlock block, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO schedule_blocks(id,calendar_id,task_id,activity_id,title_override,start_at_utc_ms,end_at_utc_ms,time_zone,recurrence_rule,recurrence_end_utc_ms,deleted_at_utc_ms,revision,created_at_utc_ms,updated_at_utc_ms)
            VALUES($id,$calendar,$task,$activity,$title,$start,$end,$zone,$rule,$recurrence_end,$deleted,$revision,$created,$updated);
            """;
        command.Parameters.AddWithValue("$id", Id(block.Id));
        command.Parameters.AddWithValue("$calendar", Id(block.CalendarId));
        command.Parameters.AddWithValue("$task", block.TaskId is null ? DBNull.Value : Id(block.TaskId.Value));
        command.Parameters.AddWithValue("$activity", block.ActivityId is null ? DBNull.Value : Id(block.ActivityId.Value));
        command.Parameters.AddWithValue("$title", block.TitleOverride is null ? DBNull.Value : block.TitleOverride);
        command.Parameters.AddWithValue("$start", block.StartAtUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$end", block.EndAtUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$zone", block.TimeZone);
        command.Parameters.AddWithValue("$rule", block.RecurrenceRule is null ? DBNull.Value : block.RecurrenceRule);
        command.Parameters.AddWithValue("$recurrence_end", block.RecurrenceEndUtc is null ? DBNull.Value : block.RecurrenceEndUtc.Value.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$deleted", block.DeletedAtUtc is null ? DBNull.Value : block.DeletedAtUtc.Value.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$revision", block.Revision);
        command.Parameters.AddWithValue("$created", nowUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$updated", nowUtc.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertCalendarEventAsync(SqliteConnection connection, SqliteTransaction transaction, CalendarEvent item, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO calendar_events(id,calendar_id,title,description,location,color,start_at_utc_ms,end_at_utc_ms,all_day,time_zone,recurrence_rule,recurrence_end_utc_ms,deleted_at_utc_ms,revision,created_at_utc_ms,updated_at_utc_ms)
            VALUES($id,$calendar,$title,$description,$location,$color,$start,$end,$all_day,$zone,$rule,$recurrence_end,$deleted,$revision,$created,$updated);
            """;
        command.Parameters.AddWithValue("$id", Id(item.Id));
        command.Parameters.AddWithValue("$calendar", Id(item.CalendarId));
        command.Parameters.AddWithValue("$title", item.Title);
        command.Parameters.AddWithValue("$description", item.Description);
        command.Parameters.AddWithValue("$location", item.Location is null ? DBNull.Value : item.Location);
        command.Parameters.AddWithValue("$color", item.Color);
        command.Parameters.AddWithValue("$start", item.StartAtUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$end", item.EndAtUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$all_day", item.AllDay ? 1 : 0);
        command.Parameters.AddWithValue("$zone", item.TimeZone);
        command.Parameters.AddWithValue("$rule", item.RecurrenceRule is null ? DBNull.Value : item.RecurrenceRule);
        command.Parameters.AddWithValue("$recurrence_end", item.RecurrenceEndUtc is null ? DBNull.Value : item.RecurrenceEndUtc.Value.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$deleted", item.DeletedAtUtc is null ? DBNull.Value : item.DeletedAtUtc.Value.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$revision", item.Revision);
        command.Parameters.AddWithValue("$created", nowUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$updated", nowUtc.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddCalendarEventParameters(SqliteCommand command, DateTimeOffset startAtUtc, DateTimeOffset endAtUtc, bool allDay, (string Title, string Description, string? Location, string Color, string TimeZone, string? RecurrenceRule) validated, DateTimeOffset? recurrenceEndUtc, long revision, Guid id, long expectedRevision, DateTimeOffset nowUtc)
    {
        command.Parameters.AddWithValue("$title", validated.Title);
        command.Parameters.AddWithValue("$description", validated.Description);
        command.Parameters.AddWithValue("$location", validated.Location is null ? DBNull.Value : validated.Location);
        command.Parameters.AddWithValue("$color", validated.Color);
        command.Parameters.AddWithValue("$start", startAtUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$end", endAtUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$all_day", allDay ? 1 : 0);
        command.Parameters.AddWithValue("$zone", validated.TimeZone);
        command.Parameters.AddWithValue("$rule", validated.RecurrenceRule is null ? DBNull.Value : validated.RecurrenceRule);
        command.Parameters.AddWithValue("$recurrence_end", recurrenceEndUtc is null ? DBNull.Value : recurrenceEndUtc.Value.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$updated", nowUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$id", Id(id));
        command.Parameters.AddWithValue("$expected", expectedRevision);
    }

    private static void AddExceptionParameters(SqliteCommand command, DateTimeOffset? newStartAtUtc, DateTimeOffset? newEndAtUtc, string? titleOverride, bool cancelled, long revision, Guid id, long expectedRevision)
    {
        command.Parameters.AddWithValue("$new_start", newStartAtUtc is null ? DBNull.Value : newStartAtUtc.Value.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$new_end", newEndAtUtc is null ? DBNull.Value : newEndAtUtc.Value.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$title", string.IsNullOrWhiteSpace(titleOverride) ? DBNull.Value : Guard.Optional(titleOverride, "titleOverride", 300));
        command.Parameters.AddWithValue("$cancelled", cancelled ? 1 : 0);
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$id", Id(id));
        command.Parameters.AddWithValue("$expected", expectedRevision);
    }

    private static async Task InsertIntervalAsync(SqliteConnection connection, SqliteTransaction transaction, Guid sessionId, TimeInterval interval, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO active_intervals(id,session_id,started_at_utc_ms,ended_at_utc_ms,source,revision) VALUES($id,$session,$started,$ended,$source,1);";
        command.Parameters.AddWithValue("$id", Id(interval.Id));
        command.Parameters.AddWithValue("$session", Id(sessionId));
        command.Parameters.AddWithValue("$started", interval.StartedAtUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$ended", interval.EndedAtUtc is null ? DBNull.Value : interval.EndedAtUtc.Value.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$source", interval.Source);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task CloseOpenIntervalAsync(SqliteConnection connection, SqliteTransaction transaction, Guid sessionId, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE active_intervals SET ended_at_utc_ms = $ended, revision = revision + 1 WHERE session_id = $session AND ended_at_utc_ms IS NULL;";
        command.Parameters.AddWithValue("$ended", nowUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$session", Id(sessionId));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new SnookException(SnookErrorCode.InvalidTransition, "The running timer has no open interval and needs recovery.");
        }
    }

    private static async Task CloseIntervalAtAsync(SqliteConnection connection, SqliteTransaction transaction, Guid intervalId, DateTimeOffset endAtUtc, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE active_intervals SET ended_at_utc_ms=$ended, revision=revision+1 WHERE id=$id AND ended_at_utc_ms IS NULL;";
        command.Parameters.AddWithValue("$ended", endAtUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$id", Id(intervalId));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new SnookException(SnookErrorCode.InvalidTransition, "The recovery interval has already been resolved.");
        }
    }

    private static void ValidateCorrection(SessionCorrection correction)
    {
        if (correction.TaskId is null && correction.ActivityId is null)
        {
            throw new SnookException(SnookErrorCode.ValidationFailed, "A corrected session needs a task or activity target.");
        }

        if (correction.Intervals.Count == 0)
        {
            throw new SnookException(SnookErrorCode.ValidationFailed, "A corrected session needs at least one interval.");
        }

        var openCount = correction.Intervals.Count(interval => interval.EndedAtUtc is null);
        if (openCount > 1)
        {
            throw new SnookException(SnookErrorCode.ValidationFailed, "A session can have at most one open interval.");
        }

        var ordered = correction.Intervals.OrderBy(interval => interval.StartedAtUtc).ToArray();
        if (ordered.Select(interval => interval.Id).Distinct().Count() != ordered.Length)
        {
            throw new SnookException(SnookErrorCode.ValidationFailed, "Interval identities must be unique.");
        }

        DateTimeOffset? previousEnd = null;
        foreach (var interval in ordered)
        {
            if (interval.EndedAtUtc is not null && interval.EndedAtUtc < interval.StartedAtUtc)
            {
                throw new SnookException(SnookErrorCode.ValidationFailed, "An interval cannot end before it starts.");
            }

            if (previousEnd is not null && interval.StartedAtUtc < previousEnd.Value)
            {
                throw new SnookException(SnookErrorCode.ValidationFailed, "Corrected intervals cannot overlap.");
            }

            previousEnd = interval.EndedAtUtc;
            if (interval.EndedAtUtc is null && interval != ordered[^1])
            {
                throw new SnookException(SnookErrorCode.ValidationFailed, "An open interval must be the final interval.");
            }
        }

        if (correction.StartedAtUtc > ordered[0].StartedAtUtc || correction.StartedAtUtc > (correction.StoppedAtUtc ?? correction.StartedAtUtc))
        {
            throw new SnookException(SnookErrorCode.ValidationFailed, "The session start must not be after its corrected boundaries.");
        }

        if (correction.StoppedAtUtc is not null && correction.StoppedAtUtc < correction.StartedAtUtc)
        {
            throw new SnookException(SnookErrorCode.ValidationFailed, "The session stop must not be before its start.");
        }
    }

    private static void ValidateRecurrence(string? recurrenceRule, DateTimeOffset? recurrenceEndUtc, DateTimeOffset startAtUtc)
    {
        if (string.IsNullOrWhiteSpace(recurrenceRule))
        {
            if (recurrenceEndUtc is not null)
            {
                throw new SnookException(SnookErrorCode.ValidationFailed, "A recurrence end requires a recurrence rule.");
            }

            return;
        }

        var values = recurrenceRule.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Split('=', 2, StringSplitOptions.TrimEntries))
            .Where(pair => pair.Length == 2)
            .ToDictionary(pair => pair[0].ToUpperInvariant(), pair => pair[1].ToUpperInvariant(), StringComparer.OrdinalIgnoreCase);
        if (!values.TryGetValue("FREQ", out var frequency) || frequency is not ("DAILY" or "WEEKLY"))
        {
            throw new SnookException(SnookErrorCode.ValidationFailed, "Recurrence supports FREQ=DAILY or FREQ=WEEKLY.");
        }

        if (values.TryGetValue("INTERVAL", out var intervalText)
            && (!int.TryParse(intervalText, CultureInfo.InvariantCulture, out var interval) || interval is < 1 or > 366))
        {
            throw new SnookException(SnookErrorCode.ValidationFailed, "Recurrence interval must be between 1 and 366.");
        }

        if (values.ContainsKey("COUNT") || values.ContainsKey("UNTIL"))
        {
            throw new SnookException(SnookErrorCode.ValidationFailed, "Use recurrenceEndUtc for a bounded recurrence; COUNT and UNTIL are not accepted in the rule.");
        }

        if (values.TryGetValue("BYDAY", out var byDay))
        {
            if (frequency != "WEEKLY" || byDay.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Any(day => day is not ("MO" or "TU" or "WE" or "TH" or "FR" or "SA" or "SU")))
            {
                throw new SnookException(SnookErrorCode.ValidationFailed, "BYDAY is supported only for weekly recurrence and must use two-letter weekdays.");
            }
        }

        if (recurrenceEndUtc is not null && recurrenceEndUtc < startAtUtc)
        {
            throw new SnookException(SnookErrorCode.ValidationFailed, "The recurrence end must not be before the first occurrence.");
        }
    }

    private static (string Title, string Description, string? Location, string Color, string TimeZone, string? RecurrenceRule) ValidateCalendarEvent(string title, string description, string? location, string color, DateTimeOffset startAtUtc, DateTimeOffset endAtUtc, string timeZone, string? recurrenceRule, DateTimeOffset? recurrenceEndUtc)
    {
        if (endAtUtc <= startAtUtc)
        {
            throw new SnookException(SnookErrorCode.ValidationFailed, "A calendar event must end after it starts.");
        }

        var validatedRule = string.IsNullOrWhiteSpace(recurrenceRule) ? null : Guard.Required(recurrenceRule, "recurrenceRule", 1_000);
        ValidateRecurrence(validatedRule, recurrenceEndUtc, startAtUtc);
        return (
            Guard.Required(title, "title", 300),
            Guard.Optional(description, "description", 20_000),
            string.IsNullOrWhiteSpace(location) ? null : Guard.Optional(location, "location", 1_000),
            ValidateColor(color),
            Guard.Required(timeZone, "timeZone", 100),
            validatedRule);
    }

    private static string ValidateColor(string color)
    {
        var validated = Guard.Required(color, "color", 32);
        if (validated[0] != '#'
            || validated.Length is not (4 or 7 or 9)
            || !validated[1..].All(character => Uri.IsHexDigit(character)))
        {
            throw new SnookException(SnookErrorCode.ValidationFailed, "Colors must be #RGB, #RRGGBB, or #RRGGBBAA.");
        }

        return validated.ToUpperInvariant();
    }

    private static async Task InsertTrackingCorrectionAsync(SqliteConnection connection, SqliteTransaction transaction, TrackingCorrection correction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO tracking_corrections(id,session_id,operation_id,reason,before_json,after_json,applied_at_utc_ms) VALUES($id,$session,$operation,$reason,$before,$after,$applied);";
        command.Parameters.AddWithValue("$id", Id(correction.Id));
        command.Parameters.AddWithValue("$session", Id(correction.SessionId));
        command.Parameters.AddWithValue("$operation", Id(correction.OperationId));
        command.Parameters.AddWithValue("$reason", correction.Reason);
        command.Parameters.AddWithValue("$before", JsonSerializer.Serialize(correction.Before));
        command.Parameters.AddWithValue("$after", JsonSerializer.Serialize(correction.After));
        command.Parameters.AddWithValue("$applied", correction.AppliedAtUtc.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task PauseRunningForegroundAsync(SqliteConnection connection, SqliteTransaction transaction, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = "SELECT id FROM tracking_sessions WHERE lane = 'foreground' AND state = 'running';";
        var ids = new List<Guid>();
        await using (var reader = await query.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                ids.Add(Guid.Parse(reader.GetString(0)));
            }
        }

        foreach (var id in ids)
        {
            await CloseOpenIntervalAsync(connection, transaction, id, nowUtc, cancellationToken);
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE tracking_sessions SET state = 'paused', updated_at_utc_ms = $updated, revision = revision + 1 WHERE id = $id AND state = 'running';";
            update.Parameters.AddWithValue("$updated", nowUtc.ToUnixTimeMilliseconds());
            update.Parameters.AddWithValue("$id", Id(id));
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static void BindTask(SqliteCommand command, TaskItem task, DateTimeOffset nowUtc)
    {
        command.Parameters.AddWithValue("$id", Id(task.Id));
        command.Parameters.AddWithValue("$project", Id(task.ProjectId));
        command.Parameters.AddWithValue("$title", task.Title);
        command.Parameters.AddWithValue("$description", task.Description);
        command.Parameters.AddWithValue("$priority", (int)task.Priority);
        command.Parameters.AddWithValue("$status", State(task.Status));
        command.Parameters.AddWithValue("$due_date", task.DueDate is null ? DBNull.Value : task.DueDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$activity", task.DefaultActivityId is null ? DBNull.Value : Id(task.DefaultActivityId.Value));
        command.Parameters.AddWithValue("$starred", task.Starred ? 1 : 0);
        command.Parameters.AddWithValue("$completed", task.CompletedAtUtc is null ? DBNull.Value : task.CompletedAtUtc.Value.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$revision", task.Revision);
        command.Parameters.AddWithValue("$created", nowUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$updated", nowUtc.ToUnixTimeMilliseconds());
    }

    private static async Task RecordMutationAsync(SqliteConnection connection, SqliteTransaction transaction, Guid operationId, string aggregateType, Guid aggregateId, string kind, long baseRevision, long newRevision, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO mutation_log(operation_id,aggregate_type,aggregate_id,base_revision,new_revision,kind,occurred_at_utc_ms) VALUES($operation,$type,$aggregate,$base,$new,$kind,$occurred);";
        command.Parameters.AddWithValue("$operation", Id(operationId));
        command.Parameters.AddWithValue("$type", aggregateType);
        command.Parameters.AddWithValue("$aggregate", Id(aggregateId));
        command.Parameters.AddWithValue("$base", baseRevision);
        command.Parameters.AddWithValue("$new", newRevision);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$occurred", nowUtc.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task RecordReceiptAsync(SqliteConnection connection, SqliteTransaction transaction, Guid operationId, Guid aggregateId, long revision, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO operation_receipts(operation_id,status,aggregate_id,aggregate_revision) VALUES($operation,'applied',$aggregate,$revision);";
        command.Parameters.AddWithValue("$operation", Id(operationId));
        command.Parameters.AddWithValue("$aggregate", Id(aggregateId));
        command.Parameters.AddWithValue("$revision", revision);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> ReceiptExistsAsync(SqliteConnection connection, SqliteTransaction transaction, Guid operationId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM operation_receipts WHERE operation_id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", Id(operationId));
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<TaskItem> ReadTaskByOperationAsync(SqliteConnection connection, SqliteTransaction transaction, Guid operationId, CancellationToken cancellationToken)
    {
        await using var receipt = connection.CreateCommand();
        receipt.Transaction = transaction;
        receipt.CommandText = "SELECT aggregate_id FROM operation_receipts WHERE operation_id = $id;";
        receipt.Parameters.AddWithValue("$id", Id(operationId));
        var id = Guid.Parse((string)(await receipt.ExecuteScalarAsync(cancellationToken) ?? throw new SnookException(SnookErrorCode.InternalError, "Operation receipt is incomplete.")));
        return await ReadTaskAnyAsync(connection, transaction, id, cancellationToken) ?? throw new SnookException(SnookErrorCode.InternalError, "Operation receipt points to a missing task.");
    }

    private static async Task<TaskLink> ReadTaskLinkByOperationAsync(SqliteConnection connection, SqliteTransaction transaction, Guid operationId, CancellationToken cancellationToken)
    {
        var id = await ReadAggregateIdByOperationAsync(connection, transaction, operationId, cancellationToken);
        return await ReadTaskLinkAsync(connection, transaction, id, cancellationToken) ?? throw new SnookException(SnookErrorCode.InternalError, "Operation receipt points to a missing task link.");
    }

    private static async Task<Board> ReadBoardByOperationAsync(SqliteConnection connection, SqliteTransaction transaction, Guid operationId, CancellationToken cancellationToken)
    {
        var id = await ReadAggregateIdByOperationAsync(connection, transaction, operationId, cancellationToken);
        return await ReadBoardAsync(connection, transaction, id, true, cancellationToken) ?? throw new SnookException(SnookErrorCode.InternalError, "Operation receipt points to a missing board.");
    }

    private static async Task<Project> ReadProjectByOperationAsync(SqliteConnection connection, SqliteTransaction transaction, Guid operationId, CancellationToken cancellationToken)
    {
        var id = await ReadAggregateIdByOperationAsync(connection, transaction, operationId, cancellationToken);
        return await ReadProjectAsync(connection, transaction, id, true, cancellationToken) ?? throw new SnookException(SnookErrorCode.InternalError, "Operation receipt points to a missing project.");
    }

    private static async Task<Activity> ReadActivityByOperationAsync(SqliteConnection connection, SqliteTransaction transaction, Guid operationId, CancellationToken cancellationToken)
    {
        var id = await ReadAggregateIdByOperationAsync(connection, transaction, operationId, cancellationToken);
        return await ReadActivityAsync(connection, transaction, id, true, cancellationToken) ?? throw new SnookException(SnookErrorCode.InternalError, "Operation receipt points to a missing activity.");
    }

    private static async Task<ActivityGroup> ReadActivityGroupByOperationAsync(SqliteConnection connection, SqliteTransaction transaction, Guid operationId, CancellationToken cancellationToken)
    {
        var id = await ReadAggregateIdByOperationAsync(connection, transaction, operationId, cancellationToken);
        return await ReadActivityGroupAsync(connection, transaction, id, true, cancellationToken) ?? throw new SnookException(SnookErrorCode.InternalError, "Operation receipt points to a missing activity group.");
    }

    private static async Task<Tag> ReadTagByOperationAsync(SqliteConnection connection, SqliteTransaction transaction, Guid operationId, CancellationToken cancellationToken)
    {
        var id = await ReadAggregateIdByOperationAsync(connection, transaction, operationId, cancellationToken);
        return await ReadTagAsync(connection, transaction, id, cancellationToken) ?? throw new SnookException(SnookErrorCode.InternalError, "Operation receipt points to a missing tag.");
    }

    private static async Task<DomainCalendar> ReadCalendarByOperationAsync(SqliteConnection connection, SqliteTransaction transaction, Guid operationId, CancellationToken cancellationToken)
    {
        var id = await ReadAggregateIdByOperationAsync(connection, transaction, operationId, cancellationToken);
        return await ReadCalendarAsync(connection, transaction, id, true, cancellationToken) ?? throw new SnookException(SnookErrorCode.InternalError, "Operation receipt points to a missing calendar.");
    }

    private static async Task<CalendarEvent> ReadCalendarEventByOperationAsync(SqliteConnection connection, SqliteTransaction transaction, Guid operationId, CancellationToken cancellationToken)
    {
        var id = await ReadAggregateIdByOperationAsync(connection, transaction, operationId, cancellationToken);
        return await ReadCalendarEventAsync(connection, transaction, id, true, cancellationToken) ?? throw new SnookException(SnookErrorCode.InternalError, "Operation receipt points to a missing calendar event.");
    }

    private static async Task<CalendarEventOccurrenceOverride> ReadCalendarEventExceptionByOperationAsync(SqliteConnection connection, SqliteTransaction transaction, Guid operationId, CancellationToken cancellationToken)
    {
        var id = await ReadAggregateIdByOperationAsync(connection, transaction, operationId, cancellationToken);
        return await ReadCalendarEventExceptionAsync(connection, transaction, id, cancellationToken) ?? throw new SnookException(SnookErrorCode.InternalError, "Operation receipt points to a missing calendar event exception.");
    }

    private static async Task<Guid> ReadAggregateIdByOperationAsync(SqliteConnection connection, SqliteTransaction transaction, Guid operationId, CancellationToken cancellationToken)
    {
        await using var receipt = connection.CreateCommand();
        receipt.Transaction = transaction;
        receipt.CommandText = "SELECT aggregate_id FROM operation_receipts WHERE operation_id = $id;";
        receipt.Parameters.AddWithValue("$id", Id(operationId));
        return Guid.Parse((string)(await receipt.ExecuteScalarAsync(cancellationToken) ?? throw new SnookException(SnookErrorCode.InternalError, "Operation receipt is incomplete.")));
    }

    private static async Task<TrackingSession> ReadSessionByOperationAsync(SqliteConnection connection, SqliteTransaction transaction, Guid operationId, CancellationToken cancellationToken)
    {
        await using var receipt = connection.CreateCommand();
        receipt.Transaction = transaction;
        receipt.CommandText = "SELECT aggregate_id FROM operation_receipts WHERE operation_id = $id;";
        receipt.Parameters.AddWithValue("$id", Id(operationId));
        var id = Guid.Parse((string)(await receipt.ExecuteScalarAsync(cancellationToken) ?? throw new SnookException(SnookErrorCode.InternalError, "Operation receipt is incomplete.")));
        return await ReadSessionAsync(connection, transaction, id, cancellationToken) ?? throw new SnookException(SnookErrorCode.InternalError, "Operation receipt points to a missing session.");
    }

    private static async Task<ScheduleBlock> ReadScheduleBlockByOperationAsync(SqliteConnection connection, SqliteTransaction transaction, Guid operationId, CancellationToken cancellationToken)
    {
        await using var receipt = connection.CreateCommand();
        receipt.Transaction = transaction;
        receipt.CommandText = "SELECT aggregate_id FROM operation_receipts WHERE operation_id = $id;";
        receipt.Parameters.AddWithValue("$id", Id(operationId));
        var id = Guid.Parse((string)(await receipt.ExecuteScalarAsync(cancellationToken) ?? throw new SnookException(SnookErrorCode.InternalError, "Operation receipt is incomplete.")));
        return await ReadScheduleBlockAsync(connection, transaction, id, cancellationToken) ?? throw new SnookException(SnookErrorCode.InternalError, "Operation receipt points to a missing schedule block.");
    }

    private static async Task<Guid> ReadWorkspaceIdForProjectAsync(SqliteConnection connection, SqliteTransaction transaction, Guid projectId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT boards.workspace_id FROM projects JOIN boards ON boards.id = projects.board_id WHERE projects.id=$project AND projects.deleted_at_utc_ms IS NULL AND boards.deleted_at_utc_ms IS NULL;";
        command.Parameters.AddWithValue("$project", Id(projectId));
        return Guid.Parse((string)(await command.ExecuteScalarAsync(cancellationToken) ?? throw new SnookException(SnookErrorCode.NotFound, "Project was not found.")));
    }

    private static async Task<Guid> ReadWorkspaceIdForActivityAsync(SqliteConnection connection, SqliteTransaction transaction, Guid activityId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT workspace_id FROM activities WHERE id=$activity AND deleted_at_utc_ms IS NULL;";
        command.Parameters.AddWithValue("$activity", Id(activityId));
        return Guid.Parse((string)(await command.ExecuteScalarAsync(cancellationToken) ?? throw new SnookException(SnookErrorCode.NotFound, "Activity was not found.")));
    }

    private static async Task EnsureProjectAsync(SqliteConnection connection, SqliteTransaction transaction, Guid id, CancellationToken cancellationToken)
    {
        await EnsureActiveExistsAsync(connection, transaction, "projects", id, "Project", cancellationToken);
    }

    private static async Task EnsureTaskAsync(SqliteConnection connection, SqliteTransaction transaction, Guid id, CancellationToken cancellationToken)
    {
        await EnsureExistsAsync(connection, transaction, "tasks", id, "Task", cancellationToken);
    }

    private static async Task EnsureActivityAsync(SqliteConnection connection, SqliteTransaction transaction, Guid id, CancellationToken cancellationToken)
    {
        await EnsureActiveExistsAsync(connection, transaction, "activities", id, "Activity", cancellationToken);
    }

    private static async Task EnsureActivityGroupAsync(SqliteConnection connection, SqliteTransaction transaction, Guid id, Guid workspaceId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM activity_groups WHERE id=$id AND workspace_id=$workspace AND deleted_at_utc_ms IS NULL;";
        command.Parameters.AddWithValue("$id", Id(id));
        command.Parameters.AddWithValue("$workspace", Id(workspaceId));
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) != 1)
        {
            throw new SnookException(SnookErrorCode.NotFound, "Activity group was not found.");
        }
    }

    private static async Task EnsureCalendarAsync(SqliteConnection connection, SqliteTransaction transaction, Guid id, CancellationToken cancellationToken)
    {
        await EnsureExistsAsync(connection, transaction, "calendars", id, "Calendar", cancellationToken);
    }

    private static async Task<bool> HasOpenPrerequisiteAsync(SqliteConnection connection, SqliteTransaction transaction, Guid taskId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM task_dependencies AS dependencies
                JOIN tasks AS prerequisite ON prerequisite.id = dependencies.prerequisite_task_id
                WHERE dependencies.task_id = $task
                  AND prerequisite.status <> 'completed'
                  AND prerequisite.deleted_at_utc_ms IS NULL);
            """;
        command.Parameters.AddWithValue("$task", Id(taskId));
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1;
    }

    private static async Task<bool> WouldCreateDependencyCycleAsync(SqliteConnection connection, SqliteTransaction transaction, Guid taskId, Guid prerequisiteTaskId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH RECURSIVE prerequisites(task_id) AS (
                SELECT prerequisite_task_id FROM task_dependencies WHERE task_id = $start
                UNION
                SELECT dependencies.prerequisite_task_id
                FROM task_dependencies AS dependencies
                JOIN prerequisites ON prerequisites.task_id = dependencies.task_id
            )
            SELECT EXISTS(SELECT 1 FROM prerequisites WHERE task_id = $target);
            """;
        command.Parameters.AddWithValue("$start", Id(prerequisiteTaskId));
        command.Parameters.AddWithValue("$target", Id(taskId));
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1;
    }

    private static async Task EnsureExistsAsync(SqliteConnection connection, SqliteTransaction transaction, string table, Guid id, string noun, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE id = $id AND deleted_at_utc_ms IS NULL;";
        command.Parameters.AddWithValue("$id", Id(id));
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) != 1)
        {
            throw new SnookException(SnookErrorCode.NotFound, $"{noun} was not found.");
        }
    }

    private static async Task EnsureActiveExistsAsync(SqliteConnection connection, SqliteTransaction transaction, string table, Guid id, string noun, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE id=$id AND deleted_at_utc_ms IS NULL AND archived_at_utc_ms IS NULL;";
        command.Parameters.AddWithValue("$id", Id(id));
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) != 1)
        {
            throw new SnookException(SnookErrorCode.NotFound, $"{noun} was not found or is archived.");
        }
    }

    private static async Task EnsureNameAvailableAsync(SqliteConnection connection, SqliteTransaction transaction, string table, string scopeColumn, Guid scopeId, string name, Guid? exceptId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT EXISTS(SELECT 1 FROM {table} WHERE {scopeColumn}=$scope AND name=$name COLLATE NOCASE AND deleted_at_utc_ms IS NULL AND ($except IS NULL OR id <> $except));";
        command.Parameters.AddWithValue("$scope", Id(scopeId));
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$except", exceptId is null ? DBNull.Value : Id(exceptId.Value));
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1)
        {
            throw new SnookException(SnookErrorCode.ValidationFailed, $"A {table.TrimEnd('s')} with that name already exists in this scope.");
        }
    }

    private static async Task<decimal> NextSortKeyAsync(SqliteConnection connection, SqliteTransaction transaction, string table, string scopeColumn, Guid scopeId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT COALESCE(MAX(sort_key), 0) + 1 FROM {table} WHERE {scopeColumn}=$scope AND deleted_at_utc_ms IS NULL;";
        command.Parameters.AddWithValue("$scope", Id(scopeId));
        return Convert.ToDecimal(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task<decimal?> FindNeighborSortKeyAsync(SqliteConnection connection, SqliteTransaction transaction, string table, string scopeColumn, Guid scopeId, decimal currentSortKey, ReorderDirection direction, string extraFilter, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var earlier = direction == ReorderDirection.Earlier;
        command.CommandText = $"SELECT sort_key FROM {table} WHERE {scopeColumn}=$scope AND deleted_at_utc_ms IS NULL {extraFilter} AND sort_key {(earlier ? "<" : ">")} $sort ORDER BY sort_key {(earlier ? "DESC" : "ASC")} LIMIT 1;";
        command.Parameters.AddWithValue("$scope", Id(scopeId));
        command.Parameters.AddWithValue("$sort", currentSortKey);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : Convert.ToDecimal(value, CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteOrganizationUpdateAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, Guid id, long expectedRevision, long nextRevision, DateTimeOffset nowUtc, object? value, CancellationToken cancellationToken, bool valueIsTimestamp = false)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$name", valueIsTimestamp ? DBNull.Value : value ?? DBNull.Value);
        command.Parameters.AddWithValue("$archived", valueIsTimestamp ? value ?? DBNull.Value : DBNull.Value);
        command.Parameters.AddWithValue("$revision", nextRevision);
        command.Parameters.AddWithValue("$id", Id(id));
        command.Parameters.AddWithValue("$expected", expectedRevision);
        command.Parameters.AddWithValue("$updated", nowUtc.ToUnixTimeMilliseconds());
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new SnookException(SnookErrorCode.RevisionConflict, "The organization item changed in another view. Refresh and try again.");
        }
    }

    private static async Task ExecuteDeletedUpdateAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, Guid id, long? deletedAtUtcMs, long expectedRevision, long nextRevision, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$deleted", deletedAtUtcMs is null ? DBNull.Value : deletedAtUtcMs.Value);
        command.Parameters.AddWithValue("$revision", nextRevision);
        command.Parameters.AddWithValue("$id", Id(id));
        command.Parameters.AddWithValue("$expected", expectedRevision);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new SnookException(SnookErrorCode.RevisionConflict, "The organization item changed in another view. Refresh and try again.");
        }
    }

    private static async Task<Guid> ReadWorkspaceIdAsync(SqliteConnection connection, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id FROM workspaces LIMIT 1;";
        return Guid.Parse((string)(await command.ExecuteScalarAsync(cancellationToken) ?? throw new SnookException(SnookErrorCode.InternalError, "Workspace is missing.")));
    }

    private static async Task<WorkspaceSettings> ReadSettingsAsync(SqliteConnection connection, Guid workspaceId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT allow_concurrent_foreground,revision FROM workspace_settings WHERE workspace_id=$workspace;";
        command.Parameters.AddWithValue("$workspace", Id(workspaceId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new WorkspaceSettings(false, 1);
        }

        return new WorkspaceSettings(reader.GetInt64(0) == 1, reader.GetInt64(1));
    }

    private static async Task<Guid> ReadWorkspaceIdForTaskAsync(SqliteConnection connection, SqliteTransaction transaction, Guid taskId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT boards.workspace_id FROM tasks JOIN projects ON projects.id = tasks.project_id JOIN boards ON boards.id = projects.board_id WHERE tasks.id = $task AND tasks.deleted_at_utc_ms IS NULL;";
        command.Parameters.AddWithValue("$task", Id(taskId));
        return Guid.Parse((string)(await command.ExecuteScalarAsync(cancellationToken) ?? throw new SnookException(SnookErrorCode.NotFound, "Task was not found.")));
    }

    private static async Task<Workspace> ReadWorkspaceAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,name,created_at_utc_ms,revision FROM workspaces LIMIT 1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new SnookException(SnookErrorCode.SchemaIncompatible, "The workspace database has no workspace row.");
        }

        return new Workspace(Guid.Parse(reader.GetString(0)), reader.GetString(1), FromMs(reader.GetInt64(2)), reader.GetInt64(3));
    }

    private static async Task<Board?> ReadBoardAsync(SqliteConnection connection, SqliteTransaction? transaction, Guid boardId, bool includeDeleted, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT id,workspace_id,name,sort_key,archived_at_utc_ms,deleted_at_utc_ms,revision FROM boards WHERE id=$id{(includeDeleted ? string.Empty : " AND deleted_at_utc_ms IS NULL")};";
        command.Parameters.AddWithValue("$id", Id(boardId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new Board(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetString(2), reader.GetDecimal(3), NullableMs(reader, 4), NullableMs(reader, 5), reader.GetInt64(6));
    }

    private static async Task<Project?> ReadProjectAsync(SqliteConnection connection, SqliteTransaction? transaction, Guid projectId, bool includeDeleted, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT id,board_id,name,description,starred,sort_key,archived_at_utc_ms,deleted_at_utc_ms,revision FROM projects WHERE id=$id{(includeDeleted ? string.Empty : " AND deleted_at_utc_ms IS NULL")};";
        command.Parameters.AddWithValue("$id", Id(projectId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new Project(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3), reader.GetInt64(4) == 1, reader.GetDecimal(5), NullableMs(reader, 6), NullableMs(reader, 7), reader.GetInt64(8));
    }

    private static async Task<Activity?> ReadActivityAsync(SqliteConnection connection, SqliteTransaction? transaction, Guid activityId, bool includeDeleted, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT id,workspace_id,name,description,lane_default,archived_at_utc_ms,deleted_at_utc_ms,group_id,revision FROM activities WHERE id=$id{(includeDeleted ? string.Empty : " AND deleted_at_utc_ms IS NULL")};";
        command.Parameters.AddWithValue("$id", Id(activityId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new Activity(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3), reader.GetString(4) == "background" ? SessionLane.Background : SessionLane.Foreground, NullableMs(reader, 5), NullableMs(reader, 6), reader.GetInt64(8), reader.IsDBNull(7) ? null : Guid.Parse(reader.GetString(7)));
    }

    private static async Task<ActivityGroup?> ReadActivityGroupAsync(SqliteConnection connection, SqliteTransaction? transaction, Guid groupId, bool includeDeleted, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT id,workspace_id,name,sort_key,deleted_at_utc_ms,revision FROM activity_groups WHERE id=$id{(includeDeleted ? string.Empty : " AND deleted_at_utc_ms IS NULL")};";
        command.Parameters.AddWithValue("$id", Id(groupId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ActivityGroup(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetString(2), reader.GetDecimal(3), NullableMs(reader, 4), reader.GetInt64(5));
    }

    private static async Task<IReadOnlyList<Board>> ReadBoardsAsync(SqliteConnection connection, Guid workspaceId, bool includeDeleted, CancellationToken cancellationToken)
    {
        var result = new List<Board>();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT id,workspace_id,name,sort_key,archived_at_utc_ms,deleted_at_utc_ms,revision FROM boards WHERE workspace_id=$workspace{(includeDeleted ? string.Empty : " AND deleted_at_utc_ms IS NULL")} ORDER BY sort_key,name;";
        command.Parameters.AddWithValue("$workspace", Id(workspaceId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new Board(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetString(2), reader.GetDecimal(3), NullableMs(reader, 4), NullableMs(reader, 5), reader.GetInt64(6)));
        }

        return result;
    }

    private static async Task<IReadOnlyList<Project>> ReadProjectsAsync(SqliteConnection connection, IEnumerable<Guid> boardIds, bool includeDeleted, CancellationToken cancellationToken)
    {
        var ids = boardIds.ToArray();
        if (ids.Length == 0)
        {
            return [];
        }

        var result = new List<Project>();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT id,board_id,name,description,starred,sort_key,archived_at_utc_ms,deleted_at_utc_ms,revision FROM projects{(includeDeleted ? string.Empty : " WHERE deleted_at_utc_ms IS NULL")} ORDER BY sort_key,name;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!ids.Contains(Guid.Parse(reader.GetString(1))))
            {
                continue;
            }

            result.Add(new Project(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3), reader.GetInt64(4) == 1, reader.GetDecimal(5), NullableMs(reader, 6), NullableMs(reader, 7), reader.GetInt64(8)));
        }

        return result;
    }

    private static async Task<IReadOnlyList<Activity>> ReadActivitiesAsync(SqliteConnection connection, Guid workspaceId, bool includeDeleted, CancellationToken cancellationToken)
    {
        var result = new List<Activity>();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT id,workspace_id,name,description,lane_default,archived_at_utc_ms,deleted_at_utc_ms,group_id,revision FROM activities WHERE workspace_id=$workspace{(includeDeleted ? string.Empty : " AND deleted_at_utc_ms IS NULL")} ORDER BY name;";
        command.Parameters.AddWithValue("$workspace", Id(workspaceId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new Activity(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3), reader.GetString(4) == "background" ? SessionLane.Background : SessionLane.Foreground, NullableMs(reader, 5), NullableMs(reader, 6), reader.GetInt64(8), reader.IsDBNull(7) ? null : Guid.Parse(reader.GetString(7))));
        }

        return result;
    }

    private static async Task<IReadOnlyList<ActivityGroup>> ReadActivityGroupsAsync(SqliteConnection connection, Guid workspaceId, bool includeDeleted, CancellationToken cancellationToken)
    {
        var result = new List<ActivityGroup>();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT id,workspace_id,name,sort_key,deleted_at_utc_ms,revision FROM activity_groups WHERE workspace_id=$workspace{(includeDeleted ? string.Empty : " AND deleted_at_utc_ms IS NULL")} ORDER BY sort_key,name;";
        command.Parameters.AddWithValue("$workspace", Id(workspaceId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new ActivityGroup(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetString(2), reader.GetDecimal(3), NullableMs(reader, 4), reader.GetInt64(5)));
        }

        return result;
    }

    private static async Task<IReadOnlyList<Tag>> ReadTagsAsync(SqliteConnection connection, Guid workspaceId, CancellationToken cancellationToken)
    {
        var result = new List<Tag>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,workspace_id,normalized_name,display_name,color,revision FROM tags WHERE workspace_id=$workspace AND deleted_at_utc_ms IS NULL ORDER BY display_name;";
        command.Parameters.AddWithValue("$workspace", Id(workspaceId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new Tag(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetInt64(5)));
        }

        return result;
    }

    private static async Task<IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>> ReadTaskTagIdsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, IReadOnlyList<Guid>>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT task_id,tag_id FROM task_tags ORDER BY task_id,tag_id;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var taskId = Guid.Parse(reader.GetString(0));
            var tagId = Guid.Parse(reader.GetString(1));
            if (!result.TryGetValue(taskId, out var ids))
            {
                ids = [];
                result.Add(taskId, ids);
            }

            result[taskId] = ids.Append(tagId).ToArray();
        }

        return result;
    }

    private static async Task<IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>> ReadTagIdsAsync(SqliteConnection connection, string membershipTable, string entityColumn, CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, IReadOnlyList<Guid>>();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {entityColumn},tag_id FROM {membershipTable} ORDER BY {entityColumn},tag_id;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var entityId = Guid.Parse(reader.GetString(0));
            var tagId = Guid.Parse(reader.GetString(1));
            var list = result.TryGetValue(entityId, out var existing) ? existing.ToList() : [];
            list.Add(tagId);
            result[entityId] = list;
        }

        return result;
    }

    private static async Task<IReadOnlyList<DomainCalendar>> ReadCalendarsAsync(SqliteConnection connection, Guid workspaceId, bool includeDeleted, CancellationToken cancellationToken)
    {
        var result = new List<DomainCalendar>();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT id,workspace_id,name,color,visible,revision,deleted_at_utc_ms FROM calendars WHERE workspace_id=$workspace{(includeDeleted ? string.Empty : " AND deleted_at_utc_ms IS NULL")} ORDER BY name;";
        command.Parameters.AddWithValue("$workspace", Id(workspaceId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new DomainCalendar(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3), reader.GetInt64(4) == 1, reader.GetInt64(5), NullableMs(reader, 6)));
        }

        return result;
    }

    private static async Task<DomainCalendar?> ReadCalendarAsync(SqliteConnection connection, SqliteTransaction? transaction, Guid calendarId, bool includeDeleted, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT id,workspace_id,name,color,visible,revision,deleted_at_utc_ms FROM calendars WHERE id=$id{(includeDeleted ? string.Empty : " AND deleted_at_utc_ms IS NULL")};";
        command.Parameters.AddWithValue("$id", Id(calendarId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new DomainCalendar(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3), reader.GetInt64(4) == 1, reader.GetInt64(5), NullableMs(reader, 6));
    }

    private static async Task<IReadOnlyList<ScheduleBlock>> ReadScheduleBlocksAsync(SqliteConnection connection, IEnumerable<Guid> calendarIds, bool includeDeleted, CancellationToken cancellationToken)
    {
        var ids = calendarIds.ToHashSet();
        var result = new List<ScheduleBlock>();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT id,calendar_id,task_id,activity_id,title_override,start_at_utc_ms,end_at_utc_ms,time_zone,recurrence_rule,recurrence_end_utc_ms,deleted_at_utc_ms,revision FROM schedule_blocks{(includeDeleted ? string.Empty : " WHERE deleted_at_utc_ms IS NULL")} ORDER BY start_at_utc_ms;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var calendarId = Guid.Parse(reader.GetString(1));
            if (!ids.Contains(calendarId))
            {
                continue;
            }

            result.Add(ReadScheduleBlock(reader));
        }

        return result;
    }

    private static ScheduleBlock ReadScheduleBlock(SqliteDataReader reader)
    {
        return new ScheduleBlock(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
            reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3)),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            FromMs(reader.GetInt64(5)),
            FromMs(reader.GetInt64(6)),
            reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            NullableMs(reader, 9),
            NullableMs(reader, 10),
            reader.GetInt64(11));
    }

    private static async Task<ScheduleBlock?> ReadScheduleBlockAsync(SqliteConnection connection, SqliteTransaction? transaction, Guid blockId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id,calendar_id,task_id,activity_id,title_override,start_at_utc_ms,end_at_utc_ms,time_zone,recurrence_rule,recurrence_end_utc_ms,deleted_at_utc_ms,revision FROM schedule_blocks WHERE id=$id AND deleted_at_utc_ms IS NULL;";
        command.Parameters.AddWithValue("$id", Id(blockId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadScheduleBlock(reader) : null;
    }

    private static async Task<IReadOnlyList<CalendarEvent>> ReadCalendarEventsAsync(SqliteConnection connection, IEnumerable<Guid> calendarIds, bool includeDeleted, CancellationToken cancellationToken)
    {
        var ids = calendarIds.ToHashSet();
        var result = new List<CalendarEvent>();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT id,calendar_id,title,description,location,color,start_at_utc_ms,end_at_utc_ms,all_day,time_zone,recurrence_rule,recurrence_end_utc_ms,deleted_at_utc_ms,revision FROM calendar_events{(includeDeleted ? string.Empty : " WHERE deleted_at_utc_ms IS NULL")} ORDER BY start_at_utc_ms;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (ids.Contains(Guid.Parse(reader.GetString(1))))
            {
                result.Add(ReadCalendarEvent(reader));
            }
        }

        return result;
    }

    private static CalendarEvent ReadCalendarEvent(SqliteDataReader reader)
        => new(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetString(5),
            FromMs(reader.GetInt64(6)),
            FromMs(reader.GetInt64(7)),
            reader.GetInt64(8) == 1,
            reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            NullableMs(reader, 11),
            NullableMs(reader, 12),
            reader.GetInt64(13));

    private static async Task<CalendarEvent?> ReadCalendarEventAsync(SqliteConnection connection, SqliteTransaction? transaction, Guid eventId, bool includeDeleted, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT id,calendar_id,title,description,location,color,start_at_utc_ms,end_at_utc_ms,all_day,time_zone,recurrence_rule,recurrence_end_utc_ms,deleted_at_utc_ms,revision FROM calendar_events WHERE id=$id{(includeDeleted ? string.Empty : " AND deleted_at_utc_ms IS NULL")};";
        command.Parameters.AddWithValue("$id", Id(eventId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadCalendarEvent(reader) : null;
    }

    private static async Task<IReadOnlyList<CalendarEventOccurrenceOverride>> ReadCalendarEventExceptionsAsync(SqliteConnection connection, IEnumerable<Guid> eventIds, CancellationToken cancellationToken)
    {
        var ids = eventIds.ToHashSet();
        var result = new List<CalendarEventOccurrenceOverride>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,event_id,original_start_at_utc_ms,new_start_at_utc_ms,new_end_at_utc_ms,title_override,cancelled,revision FROM calendar_event_exceptions ORDER BY original_start_at_utc_ms;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (ids.Contains(Guid.Parse(reader.GetString(1))))
            {
                result.Add(ReadCalendarEventException(reader));
            }
        }

        return result;
    }

    private static CalendarEventOccurrenceOverride ReadCalendarEventException(SqliteDataReader reader)
        => new(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            FromMs(reader.GetInt64(2)),
            NullableMs(reader, 3),
            NullableMs(reader, 4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetInt64(6) == 1,
            reader.GetInt64(7));

    private static async Task<CalendarEventOccurrenceOverride?> ReadCalendarEventExceptionAsync(SqliteConnection connection, SqliteTransaction? transaction, Guid exceptionId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id,event_id,original_start_at_utc_ms,new_start_at_utc_ms,new_end_at_utc_ms,title_override,cancelled,revision FROM calendar_event_exceptions WHERE id=$id;";
        command.Parameters.AddWithValue("$id", Id(exceptionId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadCalendarEventException(reader) : null;
    }

    private static async Task<CalendarEventOccurrenceOverride?> ReadCalendarEventExceptionAsync(SqliteConnection connection, SqliteTransaction? transaction, Guid eventId, DateTimeOffset originalStartAtUtc, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id,event_id,original_start_at_utc_ms,new_start_at_utc_ms,new_end_at_utc_ms,title_override,cancelled,revision FROM calendar_event_exceptions WHERE event_id=$event AND original_start_at_utc_ms=$original;";
        command.Parameters.AddWithValue("$event", Id(eventId));
        command.Parameters.AddWithValue("$original", originalStartAtUtc.ToUnixTimeMilliseconds());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadCalendarEventException(reader) : null;
    }

    private static async Task<IReadOnlyList<TaskItem>> ReadTasksAsync(SqliteConnection connection, IEnumerable<Guid> projectIds, bool includeDeleted, CancellationToken cancellationToken)
    {
        var ids = projectIds.ToHashSet();
        var result = new List<TaskItem>();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT id,project_id,title,description,priority,status,due_date,due_at_utc_ms,due_time_zone,default_activity_id,starred,completed_at_utc_ms,archived_at_utc_ms,deleted_at_utc_ms,revision FROM tasks{(includeDeleted ? string.Empty : " WHERE deleted_at_utc_ms IS NULL")} ORDER BY CASE priority WHEN 4 THEN 0 WHEN 3 THEN 1 WHEN 2 THEN 2 WHEN 1 THEN 3 ELSE 4 END, COALESCE(due_date,'9999-12-31'), title;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var projectId = Guid.Parse(reader.GetString(1));
            if (!ids.Contains(projectId))
            {
                continue;
            }

            result.Add(new TaskItem(Guid.Parse(reader.GetString(0)), projectId, reader.GetString(2), reader.GetString(3), (Priority)reader.GetInt32(4), reader.GetString(5) == "completed" ? TaskState.Completed : TaskState.Open, reader.IsDBNull(6) ? null : DateOnly.Parse(reader.GetString(6), CultureInfo.InvariantCulture), NullableMs(reader, 7), reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : Guid.Parse(reader.GetString(9)), reader.GetInt64(10) == 1, NullableMs(reader, 11), NullableMs(reader, 12), NullableMs(reader, 13), reader.GetInt64(14)));
        }

        return result;
    }

    private static async Task<IReadOnlyList<TrackingSession>> ReadSessionsAsync(SqliteConnection connection, Guid workspaceId, CancellationToken cancellationToken)
    {
        var result = new List<TrackingSession>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,workspace_id,task_id,activity_id,lane,state,started_at_utc_ms,stopped_at_utc_ms,notes,created_at_utc_ms,updated_at_utc_ms,revision,recovery_status,recovery_reason FROM tracking_sessions WHERE workspace_id=$workspace AND deleted_at_utc_ms IS NULL ORDER BY started_at_utc_ms DESC;";
        command.Parameters.AddWithValue("$workspace", Id(workspaceId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var sessionId = Guid.Parse(reader.GetString(0));
            result.Add(new TrackingSession(sessionId, Guid.Parse(reader.GetString(1)), reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)), reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3)), reader.GetString(4) == "background" ? SessionLane.Background : SessionLane.Foreground, ParseState(reader.GetString(5)), FromMs(reader.GetInt64(6)), NullableMs(reader, 7), reader.GetString(8), FromMs(reader.GetInt64(9)), FromMs(reader.GetInt64(10)), reader.GetInt64(11), await ReadIntervalsAsync(connection, sessionId, cancellationToken), reader.IsDBNull(12) ? null : reader.GetString(12), reader.IsDBNull(13) ? null : reader.GetString(13)));
        }

        return result;
    }

    private static async Task<TrackingSession?> ReadSessionAsync(SqliteConnection connection, SqliteTransaction? transaction, Guid sessionId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id,workspace_id,task_id,activity_id,lane,state,started_at_utc_ms,stopped_at_utc_ms,notes,created_at_utc_ms,updated_at_utc_ms,revision,recovery_status,recovery_reason FROM tracking_sessions WHERE id=$id AND deleted_at_utc_ms IS NULL;";
        command.Parameters.AddWithValue("$id", Id(sessionId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new TrackingSession(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)), reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3)), reader.GetString(4) == "background" ? SessionLane.Background : SessionLane.Foreground, ParseState(reader.GetString(5)), FromMs(reader.GetInt64(6)), NullableMs(reader, 7), reader.GetString(8), FromMs(reader.GetInt64(9)), FromMs(reader.GetInt64(10)), reader.GetInt64(11), await ReadIntervalsAsync(connection, sessionId, cancellationToken), reader.IsDBNull(12) ? null : reader.GetString(12), reader.IsDBNull(13) ? null : reader.GetString(13));
    }

    private static async Task<TaskItem?> ReadTaskAsync(SqliteConnection connection, SqliteTransaction? transaction, Guid taskId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id,project_id,title,description,priority,status,due_date,due_at_utc_ms,due_time_zone,default_activity_id,starred,completed_at_utc_ms,archived_at_utc_ms,deleted_at_utc_ms,revision FROM tasks WHERE id=$id AND deleted_at_utc_ms IS NULL;";
        command.Parameters.AddWithValue("$id", Id(taskId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new TaskItem(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3), (Priority)reader.GetInt32(4), reader.GetString(5) == "completed" ? TaskState.Completed : TaskState.Open, reader.IsDBNull(6) ? null : DateOnly.Parse(reader.GetString(6), CultureInfo.InvariantCulture), NullableMs(reader, 7), reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : Guid.Parse(reader.GetString(9)), reader.GetInt64(10) == 1, NullableMs(reader, 11), NullableMs(reader, 12), NullableMs(reader, 13), reader.GetInt64(14));
    }

    private static async Task<TaskItem?> ReadTaskAnyAsync(SqliteConnection connection, SqliteTransaction? transaction, Guid taskId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id,project_id,title,description,priority,status,due_date,due_at_utc_ms,due_time_zone,default_activity_id,starred,completed_at_utc_ms,archived_at_utc_ms,deleted_at_utc_ms,revision FROM tasks WHERE id=$id;";
        command.Parameters.AddWithValue("$id", Id(taskId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new TaskItem(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3), (Priority)reader.GetInt32(4), reader.GetString(5) == "completed" ? TaskState.Completed : TaskState.Open, reader.IsDBNull(6) ? null : DateOnly.Parse(reader.GetString(6), CultureInfo.InvariantCulture), NullableMs(reader, 7), reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : Guid.Parse(reader.GetString(9)), reader.GetInt64(10) == 1, NullableMs(reader, 11), NullableMs(reader, 12), NullableMs(reader, 13), reader.GetInt64(14));
    }

    private static async Task<TaskLink?> ReadTaskLinkAsync(SqliteConnection connection, SqliteTransaction? transaction, Guid linkId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id,task_id,label,uri,kind FROM task_links WHERE id=$id AND deleted_at_utc_ms IS NULL;";
        command.Parameters.AddWithValue("$id", Id(linkId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new TaskLink(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3), reader.GetString(4));
    }

    private static async Task<IReadOnlyList<TaskLink>> ReadTaskLinksAsync(SqliteConnection connection, Guid taskId, CancellationToken cancellationToken)
    {
        var result = new List<TaskLink>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,task_id,label,uri,kind FROM task_links WHERE task_id=$task AND deleted_at_utc_ms IS NULL ORDER BY created_at_utc_ms;";
        command.Parameters.AddWithValue("$task", Id(taskId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new TaskLink(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3), reader.GetString(4)));
        }

        return result;
    }

    private static async Task<IReadOnlyList<Tag>> ReadTaskTagsAsync(SqliteConnection connection, Guid taskId, CancellationToken cancellationToken)
    {
        var result = new List<Tag>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT tags.id,tags.workspace_id,tags.normalized_name,tags.display_name,tags.color,tags.revision FROM task_tags JOIN tags ON tags.id = task_tags.tag_id WHERE task_tags.task_id=$task AND tags.deleted_at_utc_ms IS NULL ORDER BY tags.display_name;";
        command.Parameters.AddWithValue("$task", Id(taskId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new Tag(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetInt64(5)));
        }

        return result;
    }

    private static async Task<IReadOnlyList<Guid>> ReadPrerequisiteIdsAsync(SqliteConnection connection, Guid taskId, CancellationToken cancellationToken)
    {
        var result = new List<Guid>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT prerequisite_task_id FROM task_dependencies WHERE task_id=$task ORDER BY created_at_utc_ms;";
        command.Parameters.AddWithValue("$task", Id(taskId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(Guid.Parse(reader.GetString(0)));
        }

        return result;
    }

    private static async Task<Tag?> ReadTagAsync(SqliteConnection connection, SqliteTransaction? transaction, Guid tagId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id,workspace_id,normalized_name,display_name,color,revision FROM tags WHERE id=$id AND deleted_at_utc_ms IS NULL;";
        command.Parameters.AddWithValue("$id", Id(tagId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new Tag(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetInt64(5));
    }

    private static async Task<Tag?> ReadTagByNameAsync(SqliteConnection connection, SqliteTransaction transaction, Guid workspaceId, string normalizedName, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id,workspace_id,normalized_name,display_name,color,revision FROM tags WHERE workspace_id=$workspace AND normalized_name=$name AND deleted_at_utc_ms IS NULL;";
        command.Parameters.AddWithValue("$workspace", Id(workspaceId));
        command.Parameters.AddWithValue("$name", normalizedName);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new Tag(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetInt64(5));
    }

    private static async Task<IReadOnlyList<TimeInterval>> ReadIntervalsAsync(SqliteConnection connection, Guid sessionId, CancellationToken cancellationToken)
    {
        var result = new List<TimeInterval>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,started_at_utc_ms,ended_at_utc_ms,source FROM active_intervals WHERE session_id=$session ORDER BY started_at_utc_ms;";
        command.Parameters.AddWithValue("$session", Id(sessionId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new TimeInterval(Guid.Parse(reader.GetString(0)), FromMs(reader.GetInt64(1)), NullableMs(reader, 2), reader.GetString(3)));
        }

        return result;
    }

    private static async Task<long> ReadCursorAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(sequence),0) FROM mutation_log;";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static string Id(Guid id) => id.ToString("D");
    private static string CsvField(string? value) => $"\"{(value ?? string.Empty).Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left <= right ? left : right;
    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) => left >= right ? left : right;
    private static bool MoveIfExists(string sourcePath, string destinationPath)
    {
        if (!File.Exists(sourcePath))
        {
            return false;
        }

        File.Move(sourcePath, destinationPath);
        return true;
    }
    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
    private static string Lane(SessionLane lane) => lane == SessionLane.Background ? "background" : "foreground";
    private static string State(SessionState state) => state switch { SessionState.Running => "running", SessionState.Paused => "paused", SessionState.Stopped => "stopped", SessionState.RecoveryRequired => "recovery-required", _ => throw new ArgumentOutOfRangeException(nameof(state)) };
    private static string State(TaskState state) => state == TaskState.Completed ? "completed" : "open";
    private static SessionState ParseState(string state) => state switch { "running" => SessionState.Running, "paused" => SessionState.Paused, "stopped" => SessionState.Stopped, "recovery-required" => SessionState.RecoveryRequired, _ => throw new SnookException(SnookErrorCode.SchemaIncompatible, "The database contains an unknown session state.") };
    private static DateTimeOffset FromMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms);
    private static DateTimeOffset? NullableMs(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : FromMs(reader.GetInt64(ordinal));
    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken) { await using var stream = File.OpenRead(path); var hash = await SHA256.HashDataAsync(stream, cancellationToken); return Convert.ToHexString(hash).ToLowerInvariant(); }
    private void EnsureInitialized() { if (!_initialized) throw new InvalidOperationException("The Snook store must be initialized before use."); }

    public async ValueTask DisposeAsync()
    {
        _lease?.Dispose();
        _lease = null;
        _writeGate.Dispose();
        await Task.CompletedTask;
    }

    private const string SchemaSql = """
        PRAGMA foreign_keys = ON;
        CREATE TABLE IF NOT EXISTS schema_migrations(sequence INTEGER PRIMARY KEY, name TEXT NOT NULL, checksum TEXT NOT NULL, applied_at_utc_ms INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS workspaces(id TEXT PRIMARY KEY, name TEXT NOT NULL, created_at_utc_ms INTEGER NOT NULL, revision INTEGER NOT NULL CHECK(revision > 0));
        CREATE TABLE IF NOT EXISTS workspace_settings(workspace_id TEXT PRIMARY KEY REFERENCES workspaces(id), allow_concurrent_foreground INTEGER NOT NULL DEFAULT 0 CHECK(allow_concurrent_foreground IN (0,1)), revision INTEGER NOT NULL CHECK(revision > 0));
        CREATE TABLE IF NOT EXISTS activity_groups(id TEXT PRIMARY KEY, workspace_id TEXT NOT NULL REFERENCES workspaces(id), name TEXT NOT NULL, sort_key REAL NOT NULL, deleted_at_utc_ms INTEGER, revision INTEGER NOT NULL CHECK(revision > 0), UNIQUE(workspace_id,name COLLATE NOCASE));
        CREATE TABLE IF NOT EXISTS boards(id TEXT PRIMARY KEY, workspace_id TEXT NOT NULL REFERENCES workspaces(id), name TEXT NOT NULL, sort_key REAL NOT NULL, archived_at_utc_ms INTEGER, deleted_at_utc_ms INTEGER, revision INTEGER NOT NULL CHECK(revision > 0), UNIQUE(workspace_id,name COLLATE NOCASE));
        CREATE TABLE IF NOT EXISTS projects(id TEXT PRIMARY KEY, board_id TEXT NOT NULL REFERENCES boards(id), name TEXT NOT NULL, description TEXT NOT NULL DEFAULT '', starred INTEGER NOT NULL DEFAULT 0 CHECK(starred IN (0,1)), sort_key REAL NOT NULL, archived_at_utc_ms INTEGER, deleted_at_utc_ms INTEGER, revision INTEGER NOT NULL CHECK(revision > 0), UNIQUE(board_id,name COLLATE NOCASE));
        CREATE TABLE IF NOT EXISTS activities(id TEXT PRIMARY KEY, workspace_id TEXT NOT NULL REFERENCES workspaces(id), name TEXT NOT NULL, description TEXT NOT NULL DEFAULT '', lane_default TEXT NOT NULL CHECK(lane_default IN ('foreground','background')), archived_at_utc_ms INTEGER, deleted_at_utc_ms INTEGER, group_id TEXT REFERENCES activity_groups(id), revision INTEGER NOT NULL CHECK(revision > 0), UNIQUE(workspace_id,name COLLATE NOCASE));
        CREATE TABLE IF NOT EXISTS calendars(id TEXT PRIMARY KEY, workspace_id TEXT NOT NULL REFERENCES workspaces(id), name TEXT NOT NULL, color TEXT NOT NULL, visible INTEGER NOT NULL DEFAULT 1 CHECK(visible IN (0,1)), deleted_at_utc_ms INTEGER, revision INTEGER NOT NULL CHECK(revision > 0), UNIQUE(workspace_id,name COLLATE NOCASE));
        CREATE TABLE IF NOT EXISTS calendar_events(id TEXT PRIMARY KEY, calendar_id TEXT NOT NULL REFERENCES calendars(id), title TEXT NOT NULL CHECK(length(title) BETWEEN 1 AND 300), description TEXT NOT NULL DEFAULT '', location TEXT, color TEXT NOT NULL, start_at_utc_ms INTEGER NOT NULL, end_at_utc_ms INTEGER NOT NULL CHECK(end_at_utc_ms > start_at_utc_ms), all_day INTEGER NOT NULL DEFAULT 0 CHECK(all_day IN (0,1)), time_zone TEXT NOT NULL, recurrence_rule TEXT, recurrence_end_utc_ms INTEGER, deleted_at_utc_ms INTEGER, revision INTEGER NOT NULL CHECK(revision > 0), created_at_utc_ms INTEGER NOT NULL, updated_at_utc_ms INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS calendar_event_exceptions(id TEXT PRIMARY KEY, event_id TEXT NOT NULL REFERENCES calendar_events(id), original_start_at_utc_ms INTEGER NOT NULL, new_start_at_utc_ms INTEGER, new_end_at_utc_ms INTEGER, title_override TEXT, cancelled INTEGER NOT NULL DEFAULT 0 CHECK(cancelled IN (0,1)), revision INTEGER NOT NULL CHECK(revision > 0), UNIQUE(event_id,original_start_at_utc_ms));
        CREATE TABLE IF NOT EXISTS tags(id TEXT PRIMARY KEY, workspace_id TEXT NOT NULL REFERENCES workspaces(id), normalized_name TEXT NOT NULL, display_name TEXT NOT NULL, color TEXT, deleted_at_utc_ms INTEGER, revision INTEGER NOT NULL CHECK(revision > 0), UNIQUE(workspace_id,normalized_name));
        CREATE TABLE IF NOT EXISTS tasks(id TEXT PRIMARY KEY, project_id TEXT NOT NULL REFERENCES projects(id), title TEXT NOT NULL CHECK(length(title) BETWEEN 1 AND 300), description TEXT NOT NULL DEFAULT '', priority INTEGER NOT NULL CHECK(priority BETWEEN 0 AND 4), status TEXT NOT NULL CHECK(status IN ('open','completed')), due_date TEXT, due_at_utc_ms INTEGER, due_time_zone TEXT, default_activity_id TEXT REFERENCES activities(id), starred INTEGER NOT NULL DEFAULT 0 CHECK(starred IN (0,1)), completed_at_utc_ms INTEGER, archived_at_utc_ms INTEGER, deleted_at_utc_ms INTEGER, revision INTEGER NOT NULL CHECK(revision > 0), created_at_utc_ms INTEGER NOT NULL, updated_at_utc_ms INTEGER NOT NULL, CHECK(NOT (due_date IS NOT NULL AND due_at_utc_ms IS NOT NULL)), CHECK((status = 'completed' AND completed_at_utc_ms IS NOT NULL) OR (status = 'open' AND completed_at_utc_ms IS NULL)));
        CREATE TABLE IF NOT EXISTS task_dependencies(id TEXT PRIMARY KEY, task_id TEXT NOT NULL REFERENCES tasks(id), prerequisite_task_id TEXT NOT NULL REFERENCES tasks(id), created_at_utc_ms INTEGER NOT NULL, revision INTEGER NOT NULL, CHECK(task_id <> prerequisite_task_id), UNIQUE(task_id,prerequisite_task_id));
        CREATE TABLE IF NOT EXISTS task_links(id TEXT PRIMARY KEY, task_id TEXT NOT NULL REFERENCES tasks(id), label TEXT, uri TEXT NOT NULL, kind TEXT NOT NULL, created_at_utc_ms INTEGER NOT NULL, deleted_at_utc_ms INTEGER, revision INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS project_tags(id TEXT PRIMARY KEY, project_id TEXT NOT NULL REFERENCES projects(id), tag_id TEXT NOT NULL REFERENCES tags(id), revision INTEGER NOT NULL, UNIQUE(project_id,tag_id));
        CREATE TABLE IF NOT EXISTS task_tags(id TEXT PRIMARY KEY, task_id TEXT NOT NULL REFERENCES tasks(id), tag_id TEXT NOT NULL REFERENCES tags(id), revision INTEGER NOT NULL, UNIQUE(task_id,tag_id));
        CREATE TABLE IF NOT EXISTS activity_tags(id TEXT PRIMARY KEY, activity_id TEXT NOT NULL REFERENCES activities(id), tag_id TEXT NOT NULL REFERENCES tags(id), revision INTEGER NOT NULL, UNIQUE(activity_id,tag_id));
        CREATE TABLE IF NOT EXISTS schedule_blocks(id TEXT PRIMARY KEY, calendar_id TEXT NOT NULL REFERENCES calendars(id), task_id TEXT REFERENCES tasks(id), activity_id TEXT REFERENCES activities(id), title_override TEXT, start_at_utc_ms INTEGER NOT NULL, end_at_utc_ms INTEGER NOT NULL CHECK(end_at_utc_ms > start_at_utc_ms), time_zone TEXT NOT NULL, recurrence_rule TEXT, recurrence_end_utc_ms INTEGER, deleted_at_utc_ms INTEGER, revision INTEGER NOT NULL CHECK(revision > 0), created_at_utc_ms INTEGER NOT NULL, updated_at_utc_ms INTEGER NOT NULL, CHECK(task_id IS NOT NULL OR activity_id IS NOT NULL));
        CREATE TABLE IF NOT EXISTS tracking_sessions(id TEXT PRIMARY KEY, workspace_id TEXT NOT NULL REFERENCES workspaces(id), task_id TEXT REFERENCES tasks(id), activity_id TEXT REFERENCES activities(id), lane TEXT NOT NULL CHECK(lane IN ('foreground','background')), state TEXT NOT NULL CHECK(state IN ('running','paused','stopped','recovery-required')), started_at_utc_ms INTEGER NOT NULL, stopped_at_utc_ms INTEGER, notes TEXT NOT NULL DEFAULT '', recovery_status TEXT, recovery_reason TEXT, created_at_utc_ms INTEGER NOT NULL, updated_at_utc_ms INTEGER NOT NULL, deleted_at_utc_ms INTEGER, revision INTEGER NOT NULL CHECK(revision > 0), CHECK(task_id IS NOT NULL OR activity_id IS NOT NULL), CHECK(stopped_at_utc_ms IS NULL OR stopped_at_utc_ms >= started_at_utc_ms));
        CREATE TABLE IF NOT EXISTS active_intervals(id TEXT PRIMARY KEY, session_id TEXT NOT NULL REFERENCES tracking_sessions(id), started_at_utc_ms INTEGER NOT NULL, ended_at_utc_ms INTEGER, source TEXT NOT NULL, revision INTEGER NOT NULL CHECK(revision > 0), CHECK(ended_at_utc_ms IS NULL OR ended_at_utc_ms >= started_at_utc_ms));
        CREATE UNIQUE INDEX IF NOT EXISTS ux_open_interval ON active_intervals(session_id) WHERE ended_at_utc_ms IS NULL;
        CREATE TABLE IF NOT EXISTS tracking_corrections(id TEXT PRIMARY KEY, session_id TEXT NOT NULL REFERENCES tracking_sessions(id), operation_id TEXT NOT NULL UNIQUE, reason TEXT NOT NULL, before_json TEXT NOT NULL, after_json TEXT NOT NULL, applied_at_utc_ms INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS operation_receipts(operation_id TEXT PRIMARY KEY, status TEXT NOT NULL, aggregate_id TEXT, aggregate_revision INTEGER, committed_cursor INTEGER, result_payload TEXT, error_code TEXT, applied_at_utc_ms INTEGER NOT NULL DEFAULT(unixepoch('now') * 1000));
        CREATE TABLE IF NOT EXISTS mutation_log(sequence INTEGER PRIMARY KEY AUTOINCREMENT, operation_id TEXT NOT NULL UNIQUE, aggregate_type TEXT NOT NULL, aggregate_id TEXT NOT NULL, base_revision INTEGER NOT NULL, new_revision INTEGER NOT NULL, kind TEXT NOT NULL, occurred_at_utc_ms INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_tasks_project_status ON tasks(project_id,status,updated_at_utc_ms);
        CREATE INDEX IF NOT EXISTS ix_sessions_task_start ON tracking_sessions(task_id,started_at_utc_ms);
        CREATE INDEX IF NOT EXISTS ix_sessions_activity_start ON tracking_sessions(activity_id,started_at_utc_ms);
        CREATE INDEX IF NOT EXISTS ix_intervals_start ON active_intervals(started_at_utc_ms);
        CREATE INDEX IF NOT EXISTS ix_calendar_events_range ON calendar_events(calendar_id,start_at_utc_ms,end_at_utc_ms);
        CREATE INDEX IF NOT EXISTS ix_calendar_event_exceptions_event ON calendar_event_exceptions(event_id,original_start_at_utc_ms);
        """;
}
