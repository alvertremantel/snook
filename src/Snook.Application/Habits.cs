using Snook.Contracts;
using Snook.Domain;
using Snook.Persistence.Sqlite;

namespace Snook.Application;

public sealed partial class SnookBackend
{
    public Task<IReadOnlyList<HabitProgress>> GetHabitsAsync(HabitQuery query, CancellationToken cancellationToken = default)
    {
        if (query is null) throw new SnookException(SnookErrorCode.ValidationFailed, "A habit query is required.");
        return _store.GetHabitsAsync(query.Days, query.ThroughDate, query.IncludeArchived, query.IncludeDeleted, _clock.GetUtcNow(), cancellationToken);
    }

    public Task<Habit> CreateHabitAsync(HabitDefinition definition, OperationRequest request, CancellationToken cancellationToken = default)
    {
        ValidateHabitRequest(request, creating: true);
        return CommitHabitAsync(_store.CreateHabitAsync(definition, request.OperationId, _clock.GetUtcNow(), cancellationToken), request, cancellationToken);
    }

    public Task<Habit> UpdateHabitAsync(Guid habitId, HabitUpdate update, OperationRequest request, CancellationToken cancellationToken = default)
    {
        ValidateHabitRequest(request);
        return CommitHabitAsync(_store.UpdateHabitAsync(habitId, update, RequireRevision(request), request.OperationId, _clock.GetUtcNow(), cancellationToken), request, cancellationToken);
    }

    public Task<Habit> SetHabitCompletionAsync(Guid habitId, DateOnly day, bool completed, OperationRequest request, CancellationToken cancellationToken = default)
    {
        ValidateHabitRequest(request);
        return CommitHabitAsync(_store.SetHabitCompletionAsync(habitId, day, completed, RequireRevision(request), request.OperationId, _clock.GetUtcNow(), cancellationToken), request, cancellationToken);
    }

    public Task<Habit> SetHabitArchivedAsync(Guid habitId, bool archived, OperationRequest request, CancellationToken cancellationToken = default)
    {
        ValidateHabitRequest(request);
        return CommitHabitAsync(_store.SetHabitArchivedAsync(habitId, archived, RequireRevision(request), request.OperationId, _clock.GetUtcNow(), cancellationToken), request, cancellationToken);
    }

    public Task<Habit> SetHabitDeletedAsync(Guid habitId, bool deleted, OperationRequest request, CancellationToken cancellationToken = default)
    {
        ValidateHabitRequest(request);
        return CommitHabitAsync(_store.SetHabitDeletedAsync(habitId, deleted, RequireRevision(request), request.OperationId, _clock.GetUtcNow(), cancellationToken), request, cancellationToken);
    }

    private async Task<Habit> CommitHabitAsync(Task<StoreHabitResult> mutation, OperationRequest request, CancellationToken cancellationToken)
    {
        var result = await mutation;
        if (result.Cursor is { } cursor)
            Changed?.Invoke(this, new ChangeNotification(cursor, request.OperationId, "habit", result.Habit.Id,
                result.Kind!, result.Habit.Revision, result.CommittedAtUtc));
        return result.Habit;
    }

    private static void ValidateHabitRequest(OperationRequest request, bool creating = false)
    {
        if (request is null || request.OperationId == Guid.Empty || request.ClientDeviceId == Guid.Empty
            || (creating ? request.ExpectedRevision is not null : request.ExpectedRevision is null or < 1))
            throw new SnookException(SnookErrorCode.ValidationFailed, creating
                ? "Provide operation and device IDs without an expected revision for a new habit."
                : "Provide operation and device IDs and the habit's expected revision.");
    }
}

public sealed partial class DaemonBackendClient
{
    public async Task<IReadOnlyList<HabitProgress>> GetHabitsAsync(HabitQuery query, CancellationToken cancellationToken = default)
        => await CallAsync<HabitProgress[]>(nameof(GetHabitsAsync), cancellationToken, query);
    public Task<Habit> CreateHabitAsync(HabitDefinition definition, OperationRequest request, CancellationToken cancellationToken = default)
        => CallAsync<Habit>(nameof(CreateHabitAsync), cancellationToken, definition, request);
    public Task<Habit> UpdateHabitAsync(Guid habitId, HabitUpdate update, OperationRequest request, CancellationToken cancellationToken = default)
        => CallAsync<Habit>(nameof(UpdateHabitAsync), cancellationToken, habitId, update, request);
    public Task<Habit> SetHabitCompletionAsync(Guid habitId, DateOnly day, bool completed, OperationRequest request, CancellationToken cancellationToken = default)
        => CallAsync<Habit>(nameof(SetHabitCompletionAsync), cancellationToken, habitId, day, completed, request);
    public Task<Habit> SetHabitArchivedAsync(Guid habitId, bool archived, OperationRequest request, CancellationToken cancellationToken = default)
        => CallAsync<Habit>(nameof(SetHabitArchivedAsync), cancellationToken, habitId, archived, request);
    public Task<Habit> SetHabitDeletedAsync(Guid habitId, bool deleted, OperationRequest request, CancellationToken cancellationToken = default)
        => CallAsync<Habit>(nameof(SetHabitDeletedAsync), cancellationToken, habitId, deleted, request);
}
