using System.Reflection;
using Snook.Contracts;
using Snook.Domain;
using Snook.UI;
using Xunit;

namespace Snook.Application.Tests;

public sealed class JournalViewModelTests
{
    [Fact]
    public async Task StartingAFilterRefreshImmediatelyInvalidatesPagination()
    {
        var backend = DispatchProxy.Create<IBackendClient, JournalBackendProxy>();
        var proxy = (JournalBackendProxy)backend;
        await using var viewModel = new MainWindowViewModel(backend);

        await viewModel.SelectSectionForScreenshotAsync("Journals", null);
        Assert.True(viewModel.HasMoreJournalEntries, $"Status: {viewModel.StatusMessage}; calls: {proxy.EntryQueryCount}");
        Assert.Equal(1, proxy.EntryQueryCount);

        viewModel.JournalSearch = "new scope";
        viewModel.FilterJournalEntriesCommand.Execute(null);

        Assert.Equal(2, proxy.EntryQueryCount);
        Assert.False(viewModel.HasMoreJournalEntries);

        // The old Load earlier button may already have dispatched its command from
        // the UI. It must not issue a request with the old scope-bound token.
        viewModel.MoreJournalEntriesCommand.Execute(null);
        Assert.Equal(2, proxy.EntryQueryCount);

        var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.JournalEntries.CollectionChanged += (_, _) => refreshed.TrySetResult();
        proxy.FilterResponse.SetResult(new JournalEntryPage([], null, false));
        await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Empty(viewModel.JournalEntries);
    }
}

public class JournalBackendProxy : DispatchProxy
{
    private readonly Journal _journal = new(Guid.NewGuid(), "Daily", "", DateTimeOffset.UtcNow, null, 1);
    private readonly JournalEntry _entry;

    public JournalBackendProxy()
    {
        _entry = new JournalEntry(Guid.NewGuid(), _journal.Id, "Earlier", "Old scope", DateTimeOffset.UtcNow,
            null, [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, 1);
    }

    public int EntryQueryCount { get; private set; }
    public TaskCompletionSource<JournalEntryPage> FilterResponse { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var method = targetMethod ?? throw new InvalidOperationException("A backend method is required.");
        switch (method.Name)
        {
            case "add_Changed":
            case "remove_Changed":
                return null;
            case nameof(IBackendClient.GetJournalsAsync):
                return Task.FromResult<IReadOnlyList<Journal>>([_journal]);
            case nameof(IBackendClient.GetJournalEntriesAsync):
                EntryQueryCount++;
                var query = Assert.IsType<JournalEntryQuery>(args![0]);
                if (EntryQueryCount == 1)
                {
                    Assert.Equal(string.Empty, query.Search);
                    return Task.FromResult(new JournalEntryPage([_entry], "old-scope-token", true));
                }

                Assert.Equal("new scope", query.Search);
                Assert.Null(query.ContinuationToken);
                return FilterResponse.Task;
            case nameof(IAsyncDisposable.DisposeAsync):
                return ValueTask.CompletedTask;
            default:
                throw new NotSupportedException($"Unexpected backend call: {method.Name}");
        }
    }
}
