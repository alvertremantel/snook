using Snook.Contracts;
using Snook.Domain;
using Snook.Persistence.Sqlite;

namespace Snook.Application;

public sealed partial class SnookBackend
{
    public Task<IReadOnlyList<Journal>> GetJournalsAsync(bool includeDeleted = false, CancellationToken cancellationToken = default)
        => _store.GetJournalsAsync(includeDeleted, cancellationToken);

    public Task<Journal> CreateJournalAsync(JournalDefinition definition, OperationRequest request, CancellationToken cancellationToken = default)
    {
        ValidateJournalRequest(request, creating: true);
        return JournalValueAsync(_store.SaveJournalAsync(null, definition, null, request.OperationId, _clock.GetUtcNow(), cancellationToken));
    }

    public Task<Journal> UpdateJournalAsync(Guid journalId, JournalDefinition definition, OperationRequest request, CancellationToken cancellationToken = default)
    {
        ValidateJournalRequest(request);
        return JournalValueAsync(_store.SaveJournalAsync(journalId, definition, request.ExpectedRevision, request.OperationId, _clock.GetUtcNow(), cancellationToken));
    }

    public Task<Journal> SetJournalDeletedAsync(Guid journalId, bool deleted, OperationRequest request, CancellationToken cancellationToken = default)
    {
        ValidateJournalRequest(request);
        return JournalValueAsync(_store.SetJournalDeletedAsync(journalId, deleted, RequireRevision(request), request.OperationId, _clock.GetUtcNow(), cancellationToken));
    }

    public async Task<JournalEntryPage> GetJournalEntriesAsync(JournalEntryQuery query, CancellationToken cancellationToken = default)
    {
        if (query is null) throw new SnookException(SnookErrorCode.ValidationFailed, "A journal entry query is required.");
        var page = await _store.GetJournalEntriesAsync(query.JournalId, query.Search, query.Tag, query.IncludeDeleted, query.PageSize, query.ContinuationToken, cancellationToken);
        return new JournalEntryPage(page.Items, page.ContinuationToken, page.HasMore);
    }

    public Task<JournalEntry> GetJournalEntryAsync(Guid entryId, CancellationToken cancellationToken = default)
        => _store.GetJournalEntryAsync(entryId, cancellationToken);
    public Task<IReadOnlyList<string>> GetJournalTagsAsync(Guid? journalId = null, CancellationToken cancellationToken = default)
        => _store.GetJournalTagsAsync(journalId, cancellationToken);

    public Task<JournalEntry> CreateJournalEntryAsync(JournalEntryDefinition definition, OperationRequest request, CancellationToken cancellationToken = default)
    {
        ValidateJournalRequest(request, creating: true);
        return JournalValueAsync(_store.SaveJournalEntryAsync(null, definition, null, request.OperationId, _clock.GetUtcNow(), cancellationToken));
    }

    public Task<JournalEntry> UpdateJournalEntryAsync(Guid entryId, JournalEntryDefinition definition, OperationRequest request, CancellationToken cancellationToken = default)
    {
        ValidateJournalRequest(request);
        return JournalValueAsync(_store.SaveJournalEntryAsync(entryId, definition, request.ExpectedRevision, request.OperationId, _clock.GetUtcNow(), cancellationToken));
    }

    public Task<JournalEntry> SetJournalEntryDeletedAsync(Guid entryId, bool deleted, OperationRequest request, CancellationToken cancellationToken = default)
    {
        ValidateJournalRequest(request);
        return JournalValueAsync(_store.SetJournalEntryDeletedAsync(entryId, deleted, RequireRevision(request), request.OperationId, _clock.GetUtcNow(), cancellationToken));
    }

    private static async Task<T> JournalValueAsync<T>(Task<StoreJournalResult<T>> mutation)
    {
        var result = await mutation;
        return result.Value;
    }

    private static void ValidateJournalRequest(OperationRequest request, bool creating = false)
    {
        if (request is null || request.OperationId == Guid.Empty || request.ClientDeviceId == Guid.Empty
            || (creating ? request.ExpectedRevision is not null : request.ExpectedRevision is null or < 1))
            throw new SnookException(SnookErrorCode.ValidationFailed, creating
                ? "Provide operation and device IDs without an expected revision for creation."
                : "Provide operation and device IDs and the journal or entry's expected revision.");
    }
}

public sealed partial class DaemonBackendClient
{
    public async Task<IReadOnlyList<Journal>> GetJournalsAsync(bool includeDeleted = false, CancellationToken cancellationToken = default)
        => await CallAsync<Journal[]>(nameof(GetJournalsAsync), cancellationToken, includeDeleted);
    public Task<Journal> CreateJournalAsync(JournalDefinition definition, OperationRequest request, CancellationToken cancellationToken = default)
        => CallAsync<Journal>(nameof(CreateJournalAsync), cancellationToken, definition, request);
    public Task<Journal> UpdateJournalAsync(Guid journalId, JournalDefinition definition, OperationRequest request, CancellationToken cancellationToken = default)
        => CallAsync<Journal>(nameof(UpdateJournalAsync), cancellationToken, journalId, definition, request);
    public Task<Journal> SetJournalDeletedAsync(Guid journalId, bool deleted, OperationRequest request, CancellationToken cancellationToken = default)
        => CallAsync<Journal>(nameof(SetJournalDeletedAsync), cancellationToken, journalId, deleted, request);
    public Task<JournalEntryPage> GetJournalEntriesAsync(JournalEntryQuery query, CancellationToken cancellationToken = default)
        => CallAsync<JournalEntryPage>(nameof(GetJournalEntriesAsync), cancellationToken, query);
    public Task<JournalEntry> GetJournalEntryAsync(Guid entryId, CancellationToken cancellationToken = default)
        => CallAsync<JournalEntry>(nameof(GetJournalEntryAsync), cancellationToken, entryId);
    public async Task<IReadOnlyList<string>> GetJournalTagsAsync(Guid? journalId = null, CancellationToken cancellationToken = default)
        => await CallAsync<string[]>(nameof(GetJournalTagsAsync), cancellationToken, journalId);
    public Task<JournalEntry> CreateJournalEntryAsync(JournalEntryDefinition definition, OperationRequest request, CancellationToken cancellationToken = default)
        => CallAsync<JournalEntry>(nameof(CreateJournalEntryAsync), cancellationToken, definition, request);
    public Task<JournalEntry> UpdateJournalEntryAsync(Guid entryId, JournalEntryDefinition definition, OperationRequest request, CancellationToken cancellationToken = default)
        => CallAsync<JournalEntry>(nameof(UpdateJournalEntryAsync), cancellationToken, entryId, definition, request);
    public Task<JournalEntry> SetJournalEntryDeletedAsync(Guid entryId, bool deleted, OperationRequest request, CancellationToken cancellationToken = default)
        => CallAsync<JournalEntry>(nameof(SetJournalEntryDeletedAsync), cancellationToken, entryId, deleted, request);
}
