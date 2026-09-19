using Snook.Contracts;
using Snook.Domain;
using Snook.Persistence.Sqlite;
using Xunit;

namespace Snook.Application.Tests;

public sealed class CallerCreateReceiptTests
{
    [Fact]
    public async Task AllConvenienceCreatesReplayOriginalResultsAfterEditsDeletionRestoreAndRestart()
    {
        var directory = Directory.CreateTempSubdirectory("snook-caller-creates-");
        try
        {
            var path = Path.Combine(directory.FullName, "workspace.db");
            ExactReceiptTests.ReceiptScenario scenario;
            await using (var backend = new SnookBackend(new SqliteStore(path)))
            {
                await backend.InitializeAsync();
                scenario = await ExerciseAsync(backend);
                var backup = await backend.CreateBackupAsync(Path.Combine(directory.FullName, "backup.db"));
                await backend.CreateBoardAsync("Outside backup");
                await backend.RestoreBackupAsync(backup.Path);
                await scenario.VerifyAsync(backend);
            }
            await using var reopened = new SnookBackend(new SqliteStore(path));
            await reopened.InitializeAsync();
            await scenario.VerifyAsync(reopened);
        }
        finally { directory.Delete(true); }
    }

    internal static async Task<ExactReceiptTests.ReceiptScenario> ExerciseAsync(IBackendClient backend)
    {
        var scenario = new ExactReceiptTests.ReceiptScenario();
        var boardRequest = Request();
        var board = await RememberAsync((client, request) => client.CreateBoardAsync("Caller board", request), boardRequest);
        var project = await RememberAsync((client, request) => client.CreateProjectAsync(board.Id, "Caller project", request: request));
        var group = await RememberAsync((client, request) => client.CreateActivityGroupAsync("Caller group", request));
        var activity = await RememberAsync((client, request) => client.CreateActivityAsync("Caller activity", groupId: group.Id, request: request));
        var calendar = await RememberAsync((client, request) => client.CreateCalendarAsync("Caller calendar", request: request));
        var task = await RememberAsync((client, request) => client.CreateTaskAsync(project.Id, "Caller task", request: request));
        var prerequisite = await backend.CreateTaskAsync(project.Id, "Caller prerequisite");
        var when = new DateTimeOffset(2026, 9, 18, 15, 0, 0, TimeSpan.Zero);
        await RememberAsync((client, request) => client.CreateCalendarEventAsync(calendar.Id, "Caller event", when, when.AddHours(1), request: request));
        await RememberAsync((client, request) => client.CreateScheduleBlockAsync(calendar.Id, task.Id, activity.Id, null, when, when.AddHours(1), "UTC", request: request));
        await RememberAsync((client, request) => client.AddTaskLinkAsync(task.Id, "Reference", "https://example.test/receipt", request: request));
        await RememberAsync((client, request) => client.AddTaskTagAsync(task.Id, "Caller task tag", request: request));
        await RememberAsync((client, request) => client.AddProjectTagAsync(project.Id, "Caller project tag", request: request));
        await RememberAsync((client, request) => client.AddActivityTagAsync(activity.Id, "Caller activity tag", request: request));
        await RememberAsync(async (client, request) =>
        {
            await client.AddTaskDependencyAsync(task.Id, prerequisite.Id, request);
            return true;
        });

        await RejectAsync(() => backend.CreateBoardAsync("Changed caller payload", boardRequest));
        await RejectAsync(() => backend.CreateActivityGroupAsync("Different method", boardRequest));
        var latestBoard = (await backend.GetBootstrapAsync()).Boards.Single(item => item.Id == board.Id);
        var updated = await backend.UpdateBoardAsync(board.Id, new BoardUpdate("Changed caller board"), Request(latestBoard.Revision));
        await backend.DeleteBoardAsync(board.Id, Request(updated.Revision));
        var latestTask = (await backend.GetTaskDetailsAsync(task.Id)).Task;
        await backend.DeleteTaskAsync(task.Id, Request(latestTask.Revision));
        await scenario.VerifyAsync(backend);
        return scenario;

        async Task<T> RememberAsync<T>(Func<IBackendClient, OperationRequest, Task<T>> create, OperationRequest? selected = null)
        {
            var request = selected ?? Request();
            var cursor = (await backend.GetBootstrapAsync()).CommittedCursor;
            await RejectAsync(() => create(backend, request with { OperationId = Guid.Empty }));
            await RejectAsync(() => create(backend, request with { ClientDeviceId = Guid.Empty }));
            await RejectAsync(() => create(backend, request with { ExpectedRevision = 1 }));
            Assert.Equal(cursor, (await backend.GetBootstrapAsync()).CommittedCursor);
            return await scenario.RememberAsync(client => create(client, request), backend);
        }
    }

    private static OperationRequest Request(long? revision = null) => new(Guid.NewGuid(), Guid.NewGuid(), revision);
    private static async Task RejectAsync(Func<Task> action)
        => Assert.Equal(SnookErrorCode.ValidationFailed, (await Assert.ThrowsAsync<SnookException>(action)).Code);
}
