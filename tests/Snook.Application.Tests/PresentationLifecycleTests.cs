using Snook.Persistence.Sqlite;
using System.Reflection;
using Snook.Contracts;
using Snook.UI;
using Snook.Domain;
using Xunit;

namespace Snook.Application.Tests;

public sealed class PresentationLifecycleTests
{
    [Fact]
    public async Task RefreshBurstRunsOneTrailingRefreshWithLatestSearch()
    {
        var directory = Directory.CreateTempSubdirectory("snook-refresh-burst-");
        try
        {
            await using var backend = new SnookBackend(new SqliteStore(Path.Combine(directory.FullName, "workspace.db")));
            await backend.InitializeAsync();
            var client = DispatchProxy.Create<IBackendClient, PresentationBackendProxy>();
            var proxy = (PresentationBackendProxy)client;
            proxy.Backend = backend;
            await using var model = new MainWindowViewModel(client, new DisplayClock(), ownsBackend: false);
            await model.InitializeAsync();
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            proxy.BlockNextBootstrap = release.Task;
            var before = proxy.BootstrapCalls;
            model.SearchText = "First search";
            await proxy.Captured.Task.WaitAsync(TimeSpan.FromSeconds(10));
            for (var index = 0; index < 30; index++) model.SearchText = $"Search {index}";
            var refresh = ((AsyncCommand)model.RefreshCommand).ExecuteAsync(null);
            Assert.False(refresh.IsCompleted);
            Assert.Equal(before + 1, proxy.BootstrapCalls);
            release.SetResult();
            await refresh.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(before + 2, proxy.BootstrapCalls);
            Assert.Equal("Search 29", model.SearchText);
        }
        finally { directory.Delete(true); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ObsoleteTaskPickerReadCannotPublishOrContinue(bool cooperative, bool dispose)
    {
        var directory = Directory.CreateTempSubdirectory("snook-picker-lifecycle-");
        try
        {
            await using var backend = new SnookBackend(new SqliteStore(Path.Combine(directory.FullName, "workspace.db")));
            await backend.InitializeAsync();
            var client = DispatchProxy.Create<IBackendClient, PresentationBackendProxy>();
            var proxy = (PresentationBackendProxy)client;
            proxy.Backend = backend;
            await using var model = new MainWindowViewModel(client, new DisplayClock(), ownsBackend: false);
            await model.InitializeAsync();
            await model.SetPresentationActiveAsync(false);
            var project = (await backend.GetBootstrapAsync()).Projects[0];
            await backend.CreateTaskAsync(project.Id, "Picker committed while hidden");
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            proxy.BlockNextMethod = nameof(IBackendClient.SearchTasksAsync);
            proxy.BlockNextResponse = release.Task;
            proxy.CooperativeCancellation = cooperative;
            var resume = model.SetPresentationActiveAsync(true);
            await proxy.Captured.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var calls = proxy.QueryCalls;
            Assert.True(proxy.BlockedToken.CanBeCanceled);
            if (dispose) await model.DisposeAsync();
            else await model.SetPresentationActiveAsync(false);
            Assert.True(proxy.BlockedToken.IsCancellationRequested);
            release.SetResult();
            await resume.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(calls, proxy.QueryCalls);
            Assert.DoesNotContain(model.ManualTaskOptions, task => task.Name == "Picker committed while hidden");
            if (!dispose)
            {
                await model.SetPresentationActiveAsync(true);
                Assert.Contains(model.ManualTaskOptions, task => task.Name == "Picker committed while hidden");
            }
        }
        finally { directory.Delete(true); }
    }

    [Theory]
    [InlineData("Tasks", nameof(IBackendClient.SearchTasksAsync))]
    [InlineData("History", nameof(IBackendClient.GetHistoryAsync))]
    [InlineData("Summary", nameof(IBackendClient.GetSummaryAsync))]
    [InlineData("Calendar", nameof(IBackendClient.GetBootstrapAsync))]
    [InlineData("Calendar", nameof(IBackendClient.GetCalendarEventsRangeAsync))]
    [InlineData("Habits", nameof(IBackendClient.GetHabitsAsync))]
    [InlineData("Journals", nameof(IBackendClient.GetJournalsAsync))]
    [InlineData("Journals", nameof(IBackendClient.GetJournalEntriesAsync))]
    public async Task HidingCancelsNavigationReadWithoutEscapingTheCommand(string section, string method)
    {
        var directory = Directory.CreateTempSubdirectory("snook-cancel-page-");
        try
        {
            await using var backend = new SnookBackend(new SqliteStore(Path.Combine(directory.FullName, "workspace.db")));
            await backend.InitializeAsync();
            var client = DispatchProxy.Create<IBackendClient, PresentationBackendProxy>();
            var proxy = (PresentationBackendProxy)client;
            proxy.Backend = backend;
            await using var model = new MainWindowViewModel(client, new DisplayClock(), ownsBackend: false);
            await model.InitializeAsync();
            proxy.BlockNextMethod = method;
            proxy.BlockNextResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
            proxy.CooperativeCancellation = true;
            var navigation = ((AsyncCommand)model.SelectSectionCommand).ExecuteAsync(section);
            await proxy.Captured.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(proxy.BlockedToken.CanBeCanceled);
            await model.SetPresentationActiveAsync(false);
            Assert.True(proxy.BlockedToken.IsCancellationRequested);
            await navigation.WaitAsync(TimeSpan.FromSeconds(10));
            await model.SetPresentationActiveAsync(true);
            Assert.True(model.IsPresentationActive);
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task HidingDoesNotCancelAnAlreadyCommittedCreationResponse()
    {
        var directory = Directory.CreateTempSubdirectory("snook-cancel-not-write-");
        try
        {
            await using var backend = new SnookBackend(new SqliteStore(Path.Combine(directory.FullName, "workspace.db")));
            await backend.InitializeAsync();
            var client = DispatchProxy.Create<IBackendClient, PresentationBackendProxy>();
            var proxy = (PresentationBackendProxy)client;
            proxy.Backend = backend;
            await using var model = new MainWindowViewModel(client, new DisplayClock(), ownsBackend: false);
            await model.InitializeAsync();
            model.TaskTitle = "Creation is not a presentation read";
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            proxy.BlockNextMethod = nameof(IBackendClient.CreateTaskAsync);
            proxy.BlockNextResponse = release.Task;
            proxy.CooperativeCancellation = true;
            var save = ((AsyncCommand)model.CreateTaskCommand).ExecuteAsync(null);
            await proxy.Captured.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await model.SetPresentationActiveAsync(false);
            Assert.False(proxy.BlockedToken.CanBeCanceled);
            Assert.False(save.IsCompleted);
            release.SetResult();
            await save.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Single(await backend.SearchTasksAsync("Creation is not a presentation read"));
        }
        finally { directory.Delete(true); }
    }

    [Theory]
    [InlineData("Tasks", nameof(IBackendClient.SearchTasksAsync))]
    [InlineData("Today", nameof(IBackendClient.GetCalendarEventsRangeAsync))]
    [InlineData("History", nameof(IBackendClient.GetHistoryAsync))]
    [InlineData("Calendar", nameof(IBackendClient.GetCalendarRangeAsync))]
    [InlineData("Summary", nameof(IBackendClient.GetSummaryAsync))]
    [InlineData("Habits", nameof(IBackendClient.GetHabitsAsync))]
    [InlineData("Journals", nameof(IBackendClient.GetJournalsAsync))]
    [InlineData("Journals", nameof(IBackendClient.GetJournalEntriesAsync))]
    public async Task HiddenPageResponseCannotPublishOrContinueItsReadChain(string section, string method)
    {
        var directory = Directory.CreateTempSubdirectory("snook-hidden-page-");
        try
        {
            await using var backend = new SnookBackend(new SqliteStore(Path.Combine(directory.FullName, "workspace.db")));
            await backend.InitializeAsync();
            var client = DispatchProxy.Create<IBackendClient, PresentationBackendProxy>();
            var proxy = (PresentationBackendProxy)client;
            proxy.Backend = backend;
            await using var model = new MainWindowViewModel(client, new DisplayClock(), ownsBackend: false);
            await model.InitializeAsync();
            await model.SelectSectionForScreenshotAsync(section, section == "Tasks" ? "list" : null);
            await model.SetPresentationActiveAsync(false);
            var project = (await backend.GetBootstrapAsync()).Projects[0];
            var task = await backend.CreateTaskAsync(project.Id, "Pending hidden page", dueDate: DateOnly.FromDateTime(DateTime.Now));
            if (section is "History" or "Summary")
                await backend.CreateManualSessionAsync(task.Id, null, DateTimeOffset.UtcNow.AddMinutes(-20), DateTimeOffset.UtcNow.AddMinutes(-10), "Hidden page fixture");
            if (section == "Habits")
                await backend.CreateHabitAsync(new HabitDefinition("Hidden habit", "", DateOnly.FromDateTime(DateTime.Now), TimeZoneInfo.Local.Id),
                    new OperationRequest(Guid.NewGuid(), Guid.NewGuid()));
            if (section == "Journals")
            {
                var journal = await backend.CreateJournalAsync(new JournalDefinition("Hidden journal"), new OperationRequest(Guid.NewGuid(), Guid.NewGuid()));
                await backend.CreateJournalEntryAsync(new JournalEntryDefinition(journal.Id, "Hidden entry", "Retain this", DateTimeOffset.UtcNow, null, []),
                    new OperationRequest(Guid.NewGuid(), Guid.NewGuid()));
            }
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            proxy.BlockNextMethod = method;
            proxy.BlockNextResponse = release.Task;
            var resume = model.SetPresentationActiveAsync(true);
            await proxy.Captured.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await model.SetPresentationActiveAsync(false);
            var calls = proxy.QueryCalls;
            release.SetResult();
            await resume;
            Assert.Equal(calls, proxy.QueryCalls);
            if (section == "Tasks") Assert.Empty(model.Tasks);
            if (section == "Today") Assert.Empty(model.TodayUpcomingRows);
            if (section == "History") Assert.Empty(model.HistoryItems);
            if (section == "Summary") Assert.Empty(model.SummaryItems);
            if (section == "Habits") Assert.Empty(model.Habits);
            if (section == "Journals") Assert.Empty(model.JournalEntries);
            if (method == nameof(IBackendClient.GetJournalsAsync)) Assert.Single(model.JournalOptions);
            await model.SetPresentationActiveAsync(true);
            Assert.Contains(model.Tasks, task => task.Title == "Pending hidden page");
            if (section == "Today") Assert.NotEmpty(model.TodayUpcomingRows);
            if (section == "History") Assert.NotEmpty(model.HistoryItems);
            if (section == "Summary") Assert.NotEmpty(model.SummaryItems);
            if (section == "Habits") Assert.Single(model.Habits);
            if (section == "Journals") Assert.Single(model.JournalEntries);
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task SnapshotFromPreviousVisibilityCannotReplaceFreshResumeState()
    {
        var directory = Directory.CreateTempSubdirectory("snook-presentation-race-");
        try
        {
            await using var backend = new SnookBackend(new SqliteStore(Path.Combine(directory.FullName, "workspace.db")));
            await backend.InitializeAsync();
            var client = DispatchProxy.Create<IBackendClient, PresentationBackendProxy>();
            var proxy = (PresentationBackendProxy)client;
            proxy.Backend = backend;
            await using var model = new MainWindowViewModel(client, new DisplayClock(), ownsBackend: false);
            await model.InitializeAsync();
            await model.SetPresentationActiveAsync(false);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            proxy.BlockNextBootstrap = release.Task;
            var earlierResume = model.SetPresentationActiveAsync(true);
            await proxy.Captured.Task.WaitAsync(TimeSpan.FromSeconds(10));
            model.SearchText = "Pending old refresh";
            await model.SetPresentationActiveAsync(false);
            await backend.CreateActivityAsync("Fresh resumed activity");
            await model.SetPresentationActiveAsync(true);
            Assert.Contains(model.Activities, activity => activity.Name == "Fresh resumed activity");
            var calls = proxy.QueryCalls;
            release.SetResult();
            await earlierResume;
            Assert.Equal(calls, proxy.QueryCalls);
            Assert.Contains(model.Activities, activity => activity.Name == "Fresh resumed activity");
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task HiddenProjectionDefersChangesAndResumeRefreshesWithoutLosingCapture()
    {
        var directory = Directory.CreateTempSubdirectory("snook-presentation-");
        try
        {
            await using var backend = new SnookBackend(new SqliteStore(Path.Combine(directory.FullName, "workspace.db")));
            await backend.InitializeAsync();
            var clock = new DisplayClock();
            await using var model = new MainWindowViewModel(backend, clock, ownsBackend: false);
            await model.InitializeAsync();
            model.TaskTitle = "Keep this unsaved capture";
            await model.SetPresentationActiveAsync(false);
            Assert.False(model.IsPresentationActive);
            Assert.Equal(Timeout.InfiniteTimeSpan, clock.DisplayTimer.Period);
            await backend.CreateActivityAsync("Created while hidden");
            Assert.DoesNotContain(model.Activities, activity => activity.Name == "Created while hidden");
            Assert.NotNull(await backend.GetBootstrapAsync()); // Hiding never closes the workspace.
            await model.SetPresentationActiveAsync(true);
            Assert.True(model.IsPresentationActive);
            Assert.Equal(TimeSpan.FromSeconds(1), clock.DisplayTimer.Period);
            Assert.Contains(model.Activities, activity => activity.Name == "Created while hidden");
            Assert.Equal("Keep this unsaved capture", model.TaskTitle);
            var changes = clock.DisplayTimer.ChangeCount;
            await model.SetPresentationActiveAsync(true);
            Assert.Equal(changes, clock.DisplayTimer.ChangeCount);
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task HiddenStartupDefersProjectionAndDisposedPresentationCannotRestart()
    {
        var directory = Directory.CreateTempSubdirectory("snook-hidden-start-");
        try
        {
            await using var backend = new SnookBackend(new SqliteStore(Path.Combine(directory.FullName, "workspace.db")));
            await backend.InitializeAsync();
            var clock = new DisplayClock();
            var model = new MainWindowViewModel(backend, clock, ownsBackend: false, presentationActive: false);
            await using (model)
            {
                await model.InitializeAsync();
                Assert.Empty(model.Activities);
                Assert.Equal(Timeout.InfiniteTimeSpan, clock.DisplayTimer.Period);
                await model.SetPresentationActiveAsync(true);
                Assert.NotEmpty(model.Activities);
            }
            Assert.True(clock.DisplayTimer.Disposed);
            var changes = clock.DisplayTimer.ChangeCount;
            await model.SetPresentationActiveAsync(true);
            Assert.False(model.IsPresentationActive);
            Assert.Equal(changes, clock.DisplayTimer.ChangeCount);
        }
        finally { directory.Delete(true); }
    }

    private sealed class DisplayClock : TimeProvider
    {
        public DisplayTimer DisplayTimer { get; } = new();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            DisplayTimer.Change(dueTime, period);
            return DisplayTimer;
        }
    }

    private sealed class DisplayTimer : ITimer
    {
        public TimeSpan Period { get; private set; }
        public int ChangeCount { get; private set; }
        public bool Disposed { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            Period = period;
            ChangeCount++;
            return true;
        }

        public void Dispose() => Disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}

public class PresentationBackendProxy : DispatchProxy
{
    public IBackendClient Backend { get; set; } = null!;
    public Task? BlockNextBootstrap { get; set; }
    public string? BlockNextMethod { get; set; }
    public Task? BlockNextResponse { get; set; }
    public bool CooperativeCancellation { get; set; }
    public CancellationToken BlockedToken { get; private set; }
    public int QueryCalls { get; private set; }
    public int BootstrapCalls { get; private set; }
    public TaskCompletionSource Captured { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        if (targetMethod.Name == nameof(IBackendClient.GetBootstrapAsync)) BootstrapCalls++;
        if (targetMethod.Name.StartsWith("Get", StringComparison.Ordinal) || targetMethod.Name.StartsWith("Search", StringComparison.Ordinal)) QueryCalls++;
        if (targetMethod.Name == BlockNextMethod && BlockNextResponse is { } responseBlock)
        {
            BlockNextResponse = null;
            var task = targetMethod.Invoke(Backend, args)!;
            BlockedToken = args?.OfType<CancellationToken>().LastOrDefault() ?? default;
            var capture = typeof(PresentationBackendProxy).GetMethod(nameof(CaptureResponseAsync), BindingFlags.Instance | BindingFlags.NonPublic)!
                .MakeGenericMethod(targetMethod.ReturnType.GetGenericArguments()[0]);
            return capture.Invoke(this, [task, responseBlock, BlockedToken]);
        }
        if (targetMethod.Name == nameof(IBackendClient.GetBootstrapAsync) && BlockNextBootstrap is { } block)
        {
            BlockNextBootstrap = null;
            return CaptureAsync(block);
        }
        return targetMethod.Invoke(Backend, args);
    }

    private async Task<BootstrapSnapshot> CaptureAsync(Task block)
    {
        var snapshot = await Backend.GetBootstrapAsync();
        Captured.SetResult();
        await block;
        return snapshot;
    }

    private async Task<T> CaptureResponseAsync<T>(Task<T> response, Task block, CancellationToken cancellationToken)
    {
        var result = await response;
        Captured.SetResult();
        if (CooperativeCancellation) await block.WaitAsync(cancellationToken);
        else await block;
        return result;
    }
}
