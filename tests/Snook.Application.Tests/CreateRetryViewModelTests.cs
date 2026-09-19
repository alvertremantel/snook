using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Windows.Input;
using Snook.Contracts;
using Snook.Domain;
using Snook.Persistence.Sqlite;
using Snook.UI;
using Xunit;

namespace Snook.Application.Tests;

public sealed class CreateRetryViewModelTests
{
    [Theory]
    [InlineData("board", nameof(IBackendClient.CreateBoardAsync))]
    [InlineData("project", nameof(IBackendClient.CreateProjectAsync))]
    [InlineData("group", nameof(IBackendClient.CreateActivityGroupAsync))]
    [InlineData("activity", nameof(IBackendClient.CreateActivityAsync))]
    [InlineData("calendar", nameof(IBackendClient.CreateCalendarAsync))]
    [InlineData("event", nameof(IBackendClient.CreateCalendarEventAsync))]
    [InlineData("manual", nameof(IBackendClient.CreateManualSessionAsync))]
    [InlineData("task", nameof(IBackendClient.CreateTaskAsync))]
    public Task RetryingAnUnchangedCreationKeepsItsRequestAndDraftAcrossRefresh(string kind, string method)
        => WithAsync(async (view, backend, proxy, clock) =>
        {
            var command = await PrepareAsync(view, kind, clock);
            proxy.DropNext = method;
            await RunAsync(command);
            Assert.Contains("Retry unchanged", view.StatusMessage, StringComparison.Ordinal);
            var draft = DraftText(view, kind);
            var cursor = (await backend.GetBootstrapAsync()).CommittedCursor;
            await RunAsync(view.RefreshCommand);
            Assert.Equal(draft, DraftText(view, kind));
            await RunAsync(command);
            Assert.Equal(cursor, (await backend.GetBootstrapAsync()).CommittedCursor);
            AssertSameAttempt(proxy, method);
        });

    [Fact]
    public Task ConfirmedTaskCreateIsClearedEvenIfItsRefreshFails()
        => WithAsync(async (view, backend, proxy, _) =>
        {
            view.TaskTitle = "Repeated intentional task";
            proxy.FailNextRead = nameof(IBackendClient.GetBootstrapAsync);
            await RunAsync(view.CreateTaskCommand);
            Assert.Empty(view.TaskTitle);
            Assert.Contains("Simulated unavailable read", view.StatusMessage, StringComparison.Ordinal);
            view.TaskTitle = "Repeated intentional task";
            await RunAsync(view.CreateTaskCommand);
            var calls = proxy.Calls.Where(call => call.Method == nameof(IBackendClient.CreateTaskAsync)).ToArray();
            Assert.Equal(2, calls.Length);
            Assert.NotEqual(calls[0].Request!.OperationId, calls[1].Request!.OperationId);
            Assert.Equal(2, (await backend.SearchTasksAsync()).Count(item => item.Task.Title == "Repeated intentional task"));
        });

    [Fact]
    public Task ATaskTypedWhileAnEarlierSaveIsPendingIsNotClearedByItsResponse()
        => WithAsync(async (view, backend, proxy, _) =>
        {
            view.TaskTitle = "First saved task";
            proxy.HoldNext = nameof(IBackendClient.CreateTaskAsync);
            var save = RunAsync(view.CreateTaskCommand);
            await proxy.Committed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            view.TaskTitle = "New draft while waiting";
            proxy.Release.SetResult();
            await save;
            Assert.Equal("New draft while waiting", view.TaskTitle);
            Assert.Contains("earlier creation was saved", view.StatusMessage, StringComparison.Ordinal);
            await RunAsync(view.CreateTaskCommand);
            Assert.Contains(await backend.SearchTasksAsync(), item => item.Task.Title == "New draft while waiting");
        });

    [Fact]
    public Task CancelledActivityAttemptCannotCloseOrOverwriteAReopenedDrawer()
        => WithAsync(async (view, backend, proxy, clock) =>
        {
            await PrepareAsync(view, "activity", clock);
            proxy.HoldNext = nameof(IBackendClient.CreateActivityAsync);
            var save = RunAsync(view.CreateActivityCommand);
            await proxy.Committed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await RunAsync(view.CloseUtilityEditorCommand);
            await RunAsync(view.OpenUtilityEditorCommand, "activity");
            view.NewActivityName = "Reopened activity draft";
            proxy.Release.SetResult();
            await save;
            Assert.True(view.IsUtilityEditorOpen);
            Assert.Equal("Reopened activity draft", view.NewActivityName);
            await RunAsync(view.CreateActivityCommand);
            Assert.False(view.IsUtilityEditorOpen);
            Assert.Contains((await backend.GetBootstrapAsync()).Activities, item => item.Name == "Reopened activity draft");
        });

    [Fact]
    public Task QuickManualRetryFreezesTheOriginalClockAndActivityChoice()
        => WithAsync(async (view, backend, proxy, clock) =>
        {
            proxy.DropNext = nameof(IBackendClient.CreateManualSessionAsync);
            await RunAsync(view.QuickManualTimeCommand);
            clock.Now = clock.Now.AddHours(1);
            view.Activities.Clear(); // A retry must not select a new activity or require a live catalog.
            var cursor = (await backend.GetBootstrapAsync()).CommittedCursor;
            await RunAsync(view.QuickManualTimeCommand);
            Assert.Equal(cursor, (await backend.GetBootstrapAsync()).CommittedCursor);
            AssertSameAttempt(proxy, nameof(IBackendClient.CreateManualSessionAsync));
            await RunAsync(view.QuickManualTimeCommand);
            var calls = proxy.Calls.Where(call => call.Method == nameof(IBackendClient.CreateManualSessionAsync)).ToArray();
            Assert.Equal(3, calls.Length);
            Assert.NotEqual(calls[1].Request!.OperationId, calls[2].Request!.OperationId);
            Assert.Equal(TimeSpan.FromHours(1), (DateTimeOffset)calls[2].Arguments[3]! - (DateTimeOffset)calls[0].Arguments[3]!);
        });

    [Fact]
    public Task ADiscardedUncertainAttemptDoesNotInviteRetryingTheNewDraft()
        => WithAsync(async (view, _, proxy, clock) =>
        {
            await PrepareAsync(view, "activity", clock);
            proxy.HoldNext = proxy.DropNext = nameof(IBackendClient.CreateActivityAsync);
            var save = RunAsync(view.CreateActivityCommand);
            await proxy.Committed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await RunAsync(view.CloseUtilityEditorCommand);
            await RunAsync(view.OpenUtilityEditorCommand, "activity");
            view.NewActivityName = "A different draft";
            proxy.Release.SetResult();
            await save;
            Assert.True(view.IsUtilityEditorOpen);
            Assert.Equal("A different draft", view.NewActivityName);
            Assert.Contains("inspect saved data", view.StatusMessage, StringComparison.Ordinal);
            Assert.DoesNotContain("Retry unchanged", view.StatusMessage, StringComparison.Ordinal);
        });

    [Fact]
    public Task PlanRetryDoesNotReevaluateTaskDefaultsOrEligibility()
        => WithAsync(async (view, backend, proxy, clock) =>
        {
            var task = await backend.CreateTaskAsync(view.SelectedProjectId, "Task to plan");
            await RunAsync(view.RefreshCommand);
            await RunAsync(view.OpenUtilityEditorCommand, "plan");
            view.CalendarPlanTaskId = task.Id;
            view.CalendarPlanStartText = Local(clock.Now.AddHours(1));
            view.CalendarPlanEndText = Local(clock.Now.AddHours(2));
            proxy.DropNext = nameof(IBackendClient.CreateScheduleBlockAsync);
            await RunAsync(view.PlanNextTaskCommand);
            clock.Zone = TimeZoneInfo.Utc;
            await backend.CompleteTaskAsync(task.Id, new OperationRequest(Guid.NewGuid(), Guid.NewGuid(), task.Revision));
            await RunAsync(view.RefreshCommand);
            var before = proxy.Calls.Count;
            await RunAsync(view.PlanNextTaskCommand);
            AssertSameAttempt(proxy, nameof(IBackendClient.CreateScheduleBlockAsync));
            Assert.Equal(nameof(IBackendClient.CreateScheduleBlockAsync), proxy.Calls[before].Method);
            Assert.False(view.IsUtilityEditorOpen);
        });

    [Theory]
    [InlineData("event", nameof(IBackendClient.CreateCalendarEventAsync))]
    [InlineData("manual", nameof(IBackendClient.CreateManualSessionAsync))]
    public Task RetryingLocalDateInputsDoesNotReinterpretTheirOriginalTimeZone(string kind, string method)
        => WithAsync(async (view, _, proxy, clock) =>
        {
            clock.Zone = TimeZoneInfo.Utc;
            var command = await PrepareAsync(view, kind, clock);
            proxy.DropNext = method;
            await RunAsync(command);
            clock.Zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
            await RunAsync(command);
            AssertSameAttempt(proxy, method);
        });

    [Fact]
    public Task TaskReferenceRetryUsesTheDraftRequestAndNeverDuplicatesTheLink()
        => WithAsync(async (view, backend, proxy, _) =>
        {
            var task = await backend.CreateTaskAsync(view.SelectedProjectId, "Task with reference");
            await RunAsync(view.RefreshCommand);
            await RunAsync(view.Tasks.Single(row => row.Task.Id == task.Id).ShowDetailsCommand);
            var editor = Assert.IsType<TaskRowViewModel>(view.TaskEditor);
            editor.LinkLabelText = "Saved once";
            editor.LinkUriText = "https://example.test/reference";
            proxy.DropNext = nameof(IBackendClient.AddTaskLinkAsync);
            await RunAsync(editor.AddLinkCommand);
            await RunAsync(view.RefreshCommand);
            Assert.Same(editor, view.TaskEditor);
            Assert.Equal("https://example.test/reference", editor.LinkUriText);
            await RunAsync(editor.AddLinkCommand);
            AssertSameAttempt(proxy, nameof(IBackendClient.AddTaskLinkAsync));
            Assert.Single((await backend.GetTaskDetailsAsync(task.Id)).Links);
        });

    [Fact]
    public Task CancellingWhilePlanDefaultsLoadPreventsSendingTheCreate()
        => WithAsync(async (view, backend, proxy, clock) =>
        {
            var task = await backend.CreateTaskAsync(view.SelectedProjectId, "Cancelled plan");
            await RunAsync(view.RefreshCommand);
            await RunAsync(view.OpenUtilityEditorCommand, "plan");
            view.CalendarPlanTaskId = task.Id;
            view.CalendarPlanStartText = Local(clock.Now.AddHours(1));
            view.CalendarPlanEndText = Local(clock.Now.AddHours(2));
            proxy.HoldNext = nameof(IBackendClient.SearchTasksAsync);
            var save = RunAsync(view.PlanNextTaskCommand);
            await proxy.Committed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await RunAsync(view.CloseUtilityEditorCommand);
            proxy.Release.SetResult();
            await save;
            Assert.DoesNotContain(proxy.Calls, call => call.Method == nameof(IBackendClient.CreateScheduleBlockAsync));
            Assert.False(view.IsUtilityEditorOpen);
            Assert.Contains("No creation was sent", view.StatusMessage, StringComparison.Ordinal);
        });

    private static async Task<ICommand> PrepareAsync(MainWindowViewModel view, string kind, MutableCreateClock clock)
    {
        switch (kind)
        {
            case "board": view.NewBoardName = "Retry board"; return view.CreateBoardCommand;
            case "project": view.NewProjectName = "Retry project"; return view.CreateProjectCommand;
            case "group": view.NewActivityGroupName = "Retry group"; return view.CreateActivityGroupCommand;
            case "activity":
                await RunAsync(view.OpenUtilityEditorCommand, "activity");
                view.NewActivityName = "Retry activity"; return view.CreateActivityCommand;
            case "calendar": view.NewCalendarName = "Retry calendar"; return view.CreateCalendarCommand;
            case "event":
                await RunAsync(view.OpenUtilityEditorCommand, "event");
                view.NewEventTitle = "Retry event";
                view.NewEventStart = Local(clock.Now.AddHours(1)); view.NewEventEnd = Local(clock.Now.AddHours(2));
                return view.CreateCalendarEventCommand;
            case "manual":
                await RunAsync(view.OpenUtilityEditorCommand, "manual");
                view.ManualActivityId = view.Activities.First(item => item.Id != Guid.Empty).Id;
                view.ManualStartText = Local(clock.Now.AddHours(-1)); view.ManualEndText = Local(clock.Now);
                view.ManualNotes = "Retry manual"; return view.ManualTimeCommand;
            case "task": view.TaskTitle = "Retry task"; return view.CreateTaskCommand;
            default: throw new ArgumentException("Unknown create scenario", nameof(kind));
        }
    }

    private static string DraftText(MainWindowViewModel view, string kind) => kind switch
    {
        "board" => view.NewBoardName, "project" => view.NewProjectName, "group" => view.NewActivityGroupName,
        "activity" => view.NewActivityName, "calendar" => view.NewCalendarName, "event" => view.NewEventTitle,
        "manual" => view.ManualNotes, "task" => view.TaskTitle, _ => throw new ArgumentException("Unknown create scenario", nameof(kind))
    };

    private static string Local(DateTimeOffset instant) => instant.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    private static async Task RunAsync(ICommand command, object? parameter = null)
    {
        Assert.True(command.CanExecute(parameter));
        await Assert.IsType<AsyncCommand>(command).ExecuteAsync(parameter).WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static void AssertSameAttempt(LostCreateResponseProxy proxy, string method)
    {
        var calls = proxy.Calls.Where(call => call.Method == method).ToArray();
        Assert.Equal(2, calls.Length);
        Assert.NotNull(calls[0].Request);
        Assert.Null(calls[0].Request!.ExpectedRevision);
        Assert.Equal(calls[0].Request, calls[1].Request);
        Assert.Equal(JsonSerializer.Serialize(calls[0].Arguments), JsonSerializer.Serialize(calls[1].Arguments));
        Assert.All(calls[0].Arguments.OfType<DateTimeOffset>(), instant => Assert.Equal(TimeSpan.Zero, instant.Offset));
    }

    private static async Task WithAsync(Func<MainWindowViewModel, SnookBackend, LostCreateResponseProxy, MutableCreateClock, Task> test)
    {
        var directory = Directory.CreateTempSubdirectory("snook-ui-create-retry-");
        try
        {
            var clock = new MutableCreateClock(new DateTimeOffset(2026, 9, 18, 15, 0, 0, TimeSpan.Zero));
            await using var backend = new SnookBackend(new SqliteStore(Path.Combine(directory.FullName, "workspace.db")), clock);
            await backend.InitializeAsync();
            await backend.CreateActivityAsync("Existing activity");
            var client = DispatchProxy.Create<IBackendClient, LostCreateResponseProxy>();
            var proxy = (LostCreateResponseProxy)client;
            proxy.Backend = backend;
            await using var view = new MainWindowViewModel(client, clock);
            await view.InitializeAsync();
            await test(view, backend, proxy, clock);
        }
        finally { directory.Delete(true); }
    }
}

public sealed class MutableCreateClock(DateTimeOffset initial) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = initial;
    public TimeZoneInfo Zone { get; set; } = TimeZoneInfo.Local;
    public override DateTimeOffset GetUtcNow() => Now;
    public override TimeZoneInfo LocalTimeZone => Zone;
}

public class LostCreateResponseProxy : DispatchProxy
{
    public IBackendClient Backend { get; set; } = null!;
    public string? DropNext { get; set; }
    public string? HoldNext { get; set; }
    public string? FailNextRead { get; set; }
    public List<CreateCall> Calls { get; } = [];
    public TaskCompletionSource Committed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var method = targetMethod ?? throw new InvalidOperationException("Missing method.");
        if (method.Name is "add_Changed" or "remove_Changed") return null;
        if (method.Name == nameof(IAsyncDisposable.DisposeAsync)) return ValueTask.CompletedTask;
        var arguments = args ?? [];
        Calls.Add(new CreateCall(method.Name, arguments.Where(value => value is not CancellationToken).ToArray(), arguments.OfType<OperationRequest>().SingleOrDefault()));
        var drop = DropNext == method.Name;
        if (drop) DropNext = null;
        var hold = HoldNext == method.Name;
        if (hold) HoldNext = null;
        var failRead = FailNextRead == method.Name;
        if (failRead) FailNextRead = null;
        var result = method.Invoke(Backend, arguments)!;
        if (!method.ReturnType.IsGenericType) return result;
        return typeof(LostCreateResponseProxy).GetMethod(nameof(FinishAsync), BindingFlags.NonPublic | BindingFlags.Instance)!
            .MakeGenericMethod(method.ReturnType.GetGenericArguments()[0]).Invoke(this, [result, drop, hold, failRead]);
    }

    private async Task<T> FinishAsync<T>(Task<T> operation, bool drop, bool hold, bool failRead)
    {
        var result = await operation;
        if (hold)
        {
            Committed.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        if (drop) throw new SnookException(SnookErrorCode.StoreUnavailable, "Simulated lost response after commit.");
        if (failRead) throw new SnookException(SnookErrorCode.StoreUnavailable, "Simulated unavailable read.");
        return result;
    }

    public sealed record CreateCall(string Method, object?[] Arguments, OperationRequest? Request);
}
