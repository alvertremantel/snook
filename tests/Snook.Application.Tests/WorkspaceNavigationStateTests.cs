using Snook.Application;
using Snook.Contracts;
using Snook.Persistence.Sqlite;
using Snook.UI;
using Xunit;

namespace Snook.Application.Tests;

public sealed class WorkspaceNavigationStateTests
{
    [Fact]
    public void CodecRejectsWrongWorkspaceUnknownValuesAndOversizeInput()
    {
        var workspace = Guid.NewGuid();
        var state = new WorkspaceNavigationState(1, workspace, "Calendar", "Board",
            Guid.NewGuid(), "Title", "History", "Month", "Flex",
            new DateOnly(2026, 9, 20), "ActivityGroup", "Activities", Guid.NewGuid());
        var encoded = state.Encode();
        Assert.Equal(state, WorkspaceNavigationState.Decode(encoded, workspace));
        Assert.Null(WorkspaceNavigationState.Decode(encoded, Guid.NewGuid()));
        Assert.Null(WorkspaceNavigationState.Decode("invalid"u8, workspace));
        Assert.Null(WorkspaceNavigationState.Decode(
            new byte[WorkspaceNavigationState.MaximumEncodedBytes + 1], workspace));
        Assert.Throws<InvalidDataException>(() => (state with { Section = "Secrets" }).Encode());
        Assert.Throws<InvalidDataException>(() => (state with { CalendarAnchor = new DateOnly(1899, 12, 31) }).Encode());
        Assert.Throws<InvalidDataException>(() => (state with { CalendarAnchor = new DateOnly(9999, 1, 1) }).Encode());
    }

    [Fact]
    public async Task RestoresDestinationAndBoundedSubviewSelections()
    {
        var directory = Directory.CreateTempSubdirectory("snook-navigation-state-");
        try
        {
            await using var backend = new SnookBackend(new SqliteStore(Path.Combine(directory.FullName, "workspace.db")));
            await backend.InitializeAsync();
            var bootstrap = await backend.GetBootstrapAsync();
            var workspace = bootstrap.Workspace.Id;
            var board = await backend.CreateBoardAsync("Navigation board");
            await backend.CreateProjectAsync(board.Id, "Navigation project");
            var journal = await backend.CreateJournalAsync(new("Navigation journal", ""),
                new OperationRequest(Guid.NewGuid(), Guid.NewGuid()));

            WorkspaceNavigationState state;
            await using (var first = new MainWindowViewModel(backend, ownsBackend: false))
            {
                await first.InitializeAsync();
                await ((AsyncCommand)first.SelectSectionCommand).ExecuteAsync("Tasks");
                await ((AsyncCommand)first.SelectTaskViewCommand).ExecuteAsync("Board");
                first.TaskBoardId = board.Id;
                first.TaskSortMode = "Title";
                await ((AsyncCommand)first.SelectSectionCommand).ExecuteAsync("Calendar");
                await ((AsyncCommand)first.SelectCalendarViewCommand).ExecuteAsync("Month");
                await ((AsyncCommand)first.SelectCalendarModeCommand).ExecuteAsync("History");
                await ((AsyncCommand)first.SelectCalendarViewCommand).ExecuteAsync("Flex");
                await ((AsyncCommand)first.NavigateCalendarCommand).ExecuteAsync("previous");
                await ((AsyncCommand)first.SelectSummaryGroupingCommand).ExecuteAsync("ActivityGroup");
                first.SelectedJournalId = journal.Id;
                await ((AsyncCommand)first.SelectSectionCommand).ExecuteAsync("Settings");
                await ((AsyncCommand)first.SelectSettingsPageCommand).ExecuteAsync("Activities");
                state = first.CaptureNavigationState(workspace);
            }

            await using var second = new MainWindowViewModel(backend, ownsBackend: false);
            await second.InitializeAsync();
            Assert.True(await second.RestoreNavigationStateAsync(state, workspace));
            Assert.Equal("Settings", second.CurrentSection);
            Assert.Equal("Activities", second.SettingsPage);
            Assert.Equal("Board", second.TaskViewMode);
            Assert.Equal(board.Id, second.TaskBoardId);
            Assert.Equal("Title", second.TaskSortMode);
            Assert.Equal("History", second.CalendarMode);
            Assert.Equal("Flex", second.CalendarView);
            Assert.Equal(state.CalendarAnchor.ToDateTime(TimeOnly.MinValue), second.CalendarAnchor);
            Assert.Equal("ActivityGroup", second.SummaryGrouping);
            Assert.Equal(journal.Id, second.SelectedJournalId);
            Assert.False(await second.RestoreNavigationStateAsync(state with { WorkspaceId = Guid.NewGuid() }, workspace));
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task MissingBoardFallsBackToAllBoards()
    {
        var directory = Directory.CreateTempSubdirectory("snook-navigation-board-");
        try
        {
            await using var backend = new SnookBackend(new SqliteStore(Path.Combine(directory.FullName, "workspace.db")));
            await backend.InitializeAsync();
            var workspace = (await backend.GetBootstrapAsync()).Workspace.Id;
            var state = new WorkspaceNavigationState(1, workspace, "Tasks", "Board",
                Guid.NewGuid(), "Priority", "Event", "Week", "Flex",
                new DateOnly(2026, 9, 20), "Project", "General", Guid.Empty);
            await using var model = new MainWindowViewModel(backend, ownsBackend: false);
            await model.InitializeAsync();
            Assert.True(await model.RestoreNavigationStateAsync(state, workspace));
            Assert.Equal(Guid.Empty, model.TaskBoardId);
            Assert.Equal("Tasks", model.CurrentSection);
            Assert.Equal("Board", model.TaskViewMode);
        }
        finally { directory.Delete(true); }
    }
}
