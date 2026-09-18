using Snook.Application;
using Snook.Contracts;
using Snook.Domain;
using Snook.Persistence.Sqlite;
using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Xunit;
using DomainCalendar = Snook.Domain.Calendar;

namespace Snook.Application.Tests;

public sealed class BackendLifecycleTests
{
    [Fact]
    public async Task TimerLifecyclePersistsOneSessionWithMultipleIntervals()
    {
        var directory = Directory.CreateTempSubdirectory("snook-test-");
        var databasePath = Path.Combine(directory.FullName, "workspace.db");
        await using var store = new SqliteStore(databasePath);
        await using var backend = new SnookBackend(store);
        await backend.InitializeAsync();
        var initial = await backend.GetBootstrapAsync();
        var project = Assert.Single(initial.Projects);
        var activity = Assert.Single(initial.Activities, item => item.DefaultLane == SessionLane.Foreground);
        var task = await backend.CreateTaskAsync(project.Id, "Write the first brief", Priority.High);

        var started = await backend.StartSessionAsync(task.Id, activity.Id, SessionLane.Foreground, Request());
        var paused = await backend.PauseSessionAsync(started.Id, Request(started.Revision));
        var resumed = await backend.ResumeSessionAsync(paused.Id, Request(paused.Revision));
        var stopped = await backend.StopSessionAsync(resumed.Id, "Done", Request(resumed.Revision));

        Assert.Equal(SessionState.Stopped, stopped.State);
        Assert.Equal("Done", stopped.Notes);
        Assert.Equal(2, stopped.Intervals.Count);
        Assert.All(stopped.Intervals, interval => Assert.NotNull(interval.EndedAtUtc));

        var reopened = await backend.GetBootstrapAsync();
        Assert.Contains(reopened.Today.PriorityTasks, item => item.Task.Id == task.Id);

        Directory.Delete(directory.FullName, recursive: true);
    }

    [Fact]
    public async Task StandaloneActivitiesCanSwitchForegroundTimersWithoutTasks()
    {
        var directory = Directory.CreateTempSubdirectory("snook-activity-switch-test-");
        var databasePath = Path.Combine(directory.FullName, "workspace.db");
        await using var store = new SqliteStore(databasePath);
        await using var backend = new SnookBackend(store);
        await backend.InitializeAsync();

        var writing = await backend.CreateActivityAsync("Writing", "Draft without a task");
        var reading = await backend.CreateActivityAsync("Reading", "Review references");
        var first = await backend.StartSessionAsync(null, writing.Id, SessionLane.Foreground, Request());
        var second = await backend.StartSessionAsync(null, reading.Id, SessionLane.Foreground, Request());

        var active = (await backend.GetBootstrapAsync()).Today.ActiveSessions;
        var pausedWriting = Assert.Single(active, item => item.Session.Id == first.Id);
        var runningReading = Assert.Single(active, item => item.Session.Id == second.Id);
        Assert.Equal(SessionState.Paused, pausedWriting.Session.State);
        Assert.Equal(SessionState.Running, runningReading.Session.State);
        Assert.Null(pausedWriting.Session.TaskId);
        Assert.Null(runningReading.Session.TaskId);
        Assert.Equal("Writing", pausedWriting.ActivityName);
        Assert.Equal("Reading", runningReading.ActivityName);

        await backend.ResumeSessionAsync(pausedWriting.Session.Id, Request(pausedWriting.Session.Revision));
        var switchedBack = (await backend.GetBootstrapAsync()).Today.ActiveSessions;
        Assert.Equal(SessionState.Running, Assert.Single(switchedBack, item => item.Session.Id == first.Id).Session.State);
        var pausedReading = Assert.Single(switchedBack, item => item.Session.Id == second.Id);
        Assert.Equal(SessionState.Paused, pausedReading.Session.State);
        Assert.Equal(2, switchedBack.Count);

        var settings = await backend.GetSettingsAsync();
        await backend.UpdateSettingsAsync(settings with { AllowConcurrentForeground = true }, Request(settings.Revision));
        await backend.ResumeSessionAsync(pausedReading.Session.Id, Request(pausedReading.Session.Revision));
        var concurrent = (await backend.GetBootstrapAsync()).Today.ActiveSessions;
        Assert.All(concurrent, item => Assert.Equal(SessionState.Running, item.Session.State));

        Directory.Delete(directory.FullName, recursive: true);
    }

    [Fact]
    public async Task RetryingCreateWithSameOperationIdReturnsTheSameTask()
    {
        var directory = Directory.CreateTempSubdirectory("snook-test-");
        var databasePath = Path.Combine(directory.FullName, "workspace.db");
        await using var store = new SqliteStore(databasePath);
        await store.InitializeAsync();
        var state = await store.LoadStateAsync(DateTimeOffset.UtcNow);
        var operationId = Guid.NewGuid();
        var first = await store.CreateTaskAsync(state.Projects[0].Id, "Idempotent capture", Priority.Medium, null, operationId, DateTimeOffset.UtcNow);
        var second = await store.CreateTaskAsync(state.Projects[0].Id, "Idempotent capture", Priority.Medium, null, operationId, DateTimeOffset.UtcNow);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.Revision, second.Revision);

        await store.DisposeAsync();
        Directory.Delete(directory.FullName, recursive: true);
    }

    [Fact]
    public async Task HistorySummaryAndScheduleBlocksUseTheSameWorkspaceContract()
    {
        var directory = Directory.CreateTempSubdirectory("snook-test-");
        var databasePath = Path.Combine(directory.FullName, "workspace.db");
        await using var store = new SqliteStore(databasePath);
        await using var backend = new SnookBackend(store);
        await backend.InitializeAsync();
        var bootstrap = await backend.GetBootstrapAsync();
        var project = Assert.Single(bootstrap.Projects);
        var activity = Assert.Single(bootstrap.Activities, item => item.DefaultLane == SessionLane.Foreground);
        var calendar = Assert.Single(await ReadCalendarsAsync(store));
        var task = await backend.CreateTaskAsync(project.Id, "Plan the next release", Priority.Medium);
        await backend.AddTaskTagAsync(task.Id, "Launch");
        await backend.AddProjectTagAsync(project.Id, "Roadmap");
        await backend.AddActivityTagAsync(activity.Id, "Deep work");
        var start = DateTimeOffset.UtcNow.AddMinutes(-35);
        var end = start.AddMinutes(25);
        await backend.CreateManualSessionAsync(task.Id, activity.Id, start, end, "Planning");
        var block = await backend.CreateScheduleBlockAsync(calendar.Id, task.Id, activity.Id, "Release planning", DateTimeOffset.UtcNow.AddHours(2), DateTimeOffset.UtcNow.AddHours(3), "UTC");
        var movedBlock = await backend.UpdateScheduleBlockAsync(block.Id, new ScheduleBlockUpdate(block.StartAtUtc.AddMinutes(15), block.EndAtUtc.AddMinutes(15), "UTC", "Moved planning", null, null), Request(block.Revision));

        var history = await backend.GetHistoryAsync(new HistoryQuery(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1), PageSize: 10));
        var summary = await backend.GetSummaryAsync(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1), SummaryGrouping.Task);
        var tagSummary = await backend.GetSummaryAsync(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1), SummaryGrouping.Tag);
        var calendarBlocks = await backend.GetCalendarRangeAsync(new CalendarRangeQuery(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1)));

        Assert.Contains(history.Items, item => item.Session.Notes == "Planning");
        Assert.Contains(summary, item => item.Label == "Plan the next release" && item.AttributedMilliseconds > 0);
        Assert.Contains(tagSummary, item => item.Label == "Launch" && item.AttributedMilliseconds > 0);
        Assert.Contains(tagSummary, item => item.Label == "Roadmap" && item.AttributedMilliseconds > 0);
        Assert.Contains(tagSummary, item => item.Label == "Deep work" && item.AttributedMilliseconds > 0);
        Assert.Contains(calendarBlocks, item => item.Id == movedBlock.Id && item.TitleOverride == "Moved planning");
        var taskDetails = await backend.GetTaskDetailsAsync(task.Id);
        var projectDetails = await backend.GetProjectDetailsAsync(project.Id);
        Assert.True(taskDetails.TrackedMilliseconds > 0);
        Assert.Equal(taskDetails.TrackedMilliseconds, projectDetails.TrackedMilliseconds);

        var dstTask = await backend.CreateTaskAsync(project.Id, "DST report");
        var dstStart = new DateTimeOffset(2026, 3, 8, 5, 30, 0, TimeSpan.Zero);
        var dstEnd = new DateTimeOffset(2026, 3, 8, 8, 30, 0, TimeSpan.Zero);
        await backend.CreateManualSessionAsync(dstTask.Id, activity.Id, dstStart, dstEnd, "DST boundary");
        var civilSummary = await backend.GetSummaryAsync(dstStart.AddMinutes(-1), dstEnd.AddMinutes(1), SummaryGrouping.Day, "America/Chicago");
        Assert.Contains(civilSummary, item => item.Key == "2026-03-07" && item.AttributedMilliseconds == 30 * 60 * 1000);
        Assert.Contains(civilSummary, item => item.Key == "2026-03-08" && item.AttributedMilliseconds == 150 * 60 * 1000);

        Directory.Delete(directory.FullName, recursive: true);
    }

    [Fact]
    public async Task ActivityGroupsAssignActivitiesAndProvideStableSummaryBuckets()
    {
        var directory = Directory.CreateTempSubdirectory("snook-test-");
        var databasePath = Path.Combine(directory.FullName, "workspace.db");
        await using var store = new SqliteStore(databasePath);
        await using var backend = new SnookBackend(store);
        await backend.InitializeAsync();

        var bootstrap = await backend.GetBootstrapAsync();
        var activity = Assert.Single(bootstrap.Activities, item => item.DefaultLane == SessionLane.Foreground);
        var project = Assert.Single(bootstrap.Projects);
        var group = await backend.CreateActivityGroupAsync("Knowledge work");
        var updatedActivity = await backend.UpdateActivityAsync(
            activity.Id,
            new ActivityUpdate(activity.Name, activity.Description, activity.DefaultLane, group.Id),
            Request(activity.Revision));
        var task = await backend.CreateTaskAsync(project.Id, "Read the design notes", Priority.Medium);
        var start = DateTimeOffset.UtcNow.AddMinutes(-20);
        await backend.CreateManualSessionAsync(task.Id, updatedActivity.Id, start, start.AddMinutes(10), "Reading");

        var grouped = await backend.GetSummaryAsync(start.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1), SummaryGrouping.ActivityGroup);

        Assert.Contains(grouped, item => item.Key == group.Id.ToString("D") && item.Label == group.Name && item.AttributedMilliseconds > 0);
        Assert.Equal(group.Id, updatedActivity.GroupId);
        var deleted = await backend.DeleteActivityGroupAsync(group.Id, Request(group.Revision));
        Assert.DoesNotContain((await backend.GetBootstrapAsync()).ActivityGroups!, item => item.Id == group.Id);
        var restored = await backend.RestoreDeletedActivityGroupAsync(group.Id, Request(deleted.Revision));
        Assert.Equal(group.Id, restored.Id);

        Directory.Delete(directory.FullName, recursive: true);
    }

    [Fact]
    public async Task BoardsProjectsAndActivityGroupsCanBeReorderedTransactionally()
    {
        var directory = Directory.CreateTempSubdirectory("snook-test-");
        var databasePath = Path.Combine(directory.FullName, "workspace.db");
        await using var store = new SqliteStore(databasePath);
        await using var backend = new SnookBackend(store);
        await backend.InitializeAsync();

        var initial = await backend.GetBootstrapAsync();
        var firstBoard = Assert.Single(initial.Boards);
        var secondBoard = await backend.CreateBoardAsync("Second board");
        var reorderBoardRequest = Request(secondBoard.Revision);
        var movedBoard = await backend.ReorderBoardAsync(secondBoard.Id, ReorderDirection.Earlier, reorderBoardRequest);
        var retriedBoard = await backend.ReorderBoardAsync(secondBoard.Id, ReorderDirection.Earlier, reorderBoardRequest);
        var afterBoards = (await backend.GetBootstrapAsync()).Boards;
        Assert.Equal(secondBoard.Id, afterBoards[0].Id);
        Assert.Equal(movedBoard.Revision, retriedBoard.Revision);

        var firstProject = Assert.Single((await backend.GetBootstrapAsync()).Projects, item => item.BoardId == firstBoard.Id);
        var secondProject = await backend.CreateProjectAsync(firstBoard.Id, "Second project");
        await backend.ReorderProjectAsync(secondProject.Id, ReorderDirection.Earlier, Request(secondProject.Revision));
        var afterProjects = (await backend.GetBootstrapAsync()).Projects.Where(item => item.BoardId == firstBoard.Id).ToArray();
        Assert.Equal(secondProject.Id, afterProjects[0].Id);
        Assert.Equal(firstProject.Id, afterProjects[1].Id);

        var firstGroup = await backend.CreateActivityGroupAsync("First group");
        var secondGroup = await backend.CreateActivityGroupAsync("Second group");
        await backend.ReorderActivityGroupAsync(secondGroup.Id, ReorderDirection.Earlier, Request(secondGroup.Revision));
        var afterGroups = (await backend.GetBootstrapAsync()).ActivityGroups!;
        Assert.Equal(secondGroup.Id, afterGroups[0].Id);
        Assert.Equal(firstGroup.Id, afterGroups[1].Id);

        Directory.Delete(directory.FullName, recursive: true);
    }

    [Fact]
    public async Task CalendarExpandsBoundedDailyRecurrenceAndRejectsUnboundedRules()
    {
        var directory = Directory.CreateTempSubdirectory("snook-test-");
        var databasePath = Path.Combine(directory.FullName, "workspace.db");
        await using var store = new SqliteStore(databasePath);
        await using var backend = new SnookBackend(store);
        await backend.InitializeAsync();
        var bootstrap = await backend.GetBootstrapAsync();
        var calendar = Assert.Single(bootstrap.Calendars);
        var activity = Assert.Single(bootstrap.Activities, item => item.DefaultLane == SessionLane.Foreground);
        var start = DateTimeOffset.UtcNow.AddHours(1);
        var recurring = await backend.CreateScheduleBlockAsync(calendar.Id, null, activity.Id, "Daily focus", start, start.AddMinutes(30), "UTC", "FREQ=DAILY;INTERVAL=1", start.AddDays(3));

        var occurrences = await backend.GetCalendarRangeAsync(new CalendarRangeQuery(start.AddMinutes(-1), start.AddDays(5), MaximumOccurrences: 20));
        var matching = occurrences.Where(item => item.Id == recurring.Id).OrderBy(item => item.StartAtUtc).ToArray();
        Assert.Equal(4, matching.Length);
        var persistedStart = DateTimeOffset.FromUnixTimeMilliseconds(start.ToUnixTimeMilliseconds());
        Assert.Equal(persistedStart.AddDays(3), matching[^1].StartAtUtc);

        var invalid = await Assert.ThrowsAsync<SnookException>(() => backend.CreateScheduleBlockAsync(calendar.Id, null, activity.Id, "Bad recurrence", start, start.AddMinutes(30), "UTC", "FREQ=DAILY;COUNT=100"));
        Assert.Equal(SnookErrorCode.ValidationFailed, invalid.Code);

        Directory.Delete(directory.FullName, recursive: true);
    }

    [Fact]
    public async Task CalendarRecurrenceSkipsDstGapsAndUsesOneFoldOccurrence()
    {
        var directory = Directory.CreateTempSubdirectory("snook-calendar-dst-test-");
        var databasePath = Path.Combine(directory.FullName, "workspace.db");
        await using var store = new SqliteStore(databasePath);
        await using var backend = new SnookBackend(store);
        await backend.InitializeAsync();
        var bootstrap = await backend.GetBootstrapAsync();
        var calendar = Assert.Single(bootstrap.Calendars);
        var activity = Assert.Single(bootstrap.Activities, item => item.DefaultLane == SessionLane.Foreground);
        const string chicago = "America/Chicago";

        var springStart = new DateTimeOffset(2026, 3, 7, 8, 30, 0, TimeSpan.Zero);
        var spring = await backend.CreateScheduleBlockAsync(calendar.Id, null, activity.Id, "Spring transition", springStart, springStart.AddMinutes(30), chicago, "FREQ=DAILY;INTERVAL=1", springStart.AddDays(2));
        var springOccurrences = (await backend.GetCalendarRangeAsync(new CalendarRangeQuery(
            new DateTimeOffset(2026, 3, 7, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 3, 10, 0, 0, 0, TimeSpan.Zero),
            MaximumOccurrences: 20)))
            .Where(item => item.Id == spring.Id)
            .OrderBy(item => item.StartAtUtc)
            .ToArray();

        Assert.Equal(2, springOccurrences.Length);
        Assert.Equal(new DateTimeOffset(2026, 3, 7, 8, 30, 0, TimeSpan.Zero), springOccurrences[0].StartAtUtc);
        Assert.Equal(new DateTimeOffset(2026, 3, 9, 7, 30, 0, TimeSpan.Zero), springOccurrences[1].StartAtUtc);

        var fallStart = new DateTimeOffset(2026, 10, 31, 6, 30, 0, TimeSpan.Zero);
        var fall = await backend.CreateScheduleBlockAsync(calendar.Id, null, activity.Id, "Fall transition", fallStart, fallStart.AddMinutes(30), chicago, "FREQ=DAILY;INTERVAL=1", fallStart.AddDays(3));
        var fallOccurrences = (await backend.GetCalendarRangeAsync(new CalendarRangeQuery(
            new DateTimeOffset(2026, 10, 31, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 3, 0, 0, 0, TimeSpan.Zero),
            MaximumOccurrences: 20)))
            .Where(item => item.Id == fall.Id)
            .OrderBy(item => item.StartAtUtc)
            .ToArray();

        Assert.Equal(3, fallOccurrences.Length);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 7, 30, 0, TimeSpan.Zero), fallOccurrences[1].StartAtUtc);

        Directory.Delete(directory.FullName, recursive: true);
    }

    [Fact]
    public async Task CalendarEventsSupportFieldsRecurrenceExceptionsAndSoftDelete()
    {
        var directory = Directory.CreateTempSubdirectory("snook-test-");
        var databasePath = Path.Combine(directory.FullName, "workspace.db");
        await using var store = new SqliteStore(databasePath);
        await using var backend = new SnookBackend(store);
        await backend.InitializeAsync();

        var calendar = await backend.CreateCalendarAsync("Personal", "#12ABEF", visible: true);
        var start = DateTimeOffset.UtcNow.AddHours(2);
        var item = await backend.CreateCalendarEventAsync(
            calendar.Id,
            "Standup",
            start,
            start.AddMinutes(30),
            "Daily sync",
            "Room 4",
            "#12ABEF",
            allDay: false,
            timeZone: "UTC",
            recurrenceRule: "FREQ=DAILY;INTERVAL=1",
            recurrenceEndUtc: start.AddDays(3));
        item = await backend.UpdateCalendarEventAsync(
            item.Id,
            new CalendarEventUpdate(
                "Updated standup",
                "Updated daily sync",
                "Room 5",
                "#34C58A",
                start,
                start.AddMinutes(30),
                false,
                "UTC",
                "FREQ=DAILY;INTERVAL=1",
                start.AddDays(3)),
            Request(item.Revision));
        Assert.Equal("Updated standup", item.Title);
        Assert.Equal("Room 5", item.Location);
        Assert.Equal("#34C58A", item.Color);

        var occurrences = await backend.GetCalendarEventsRangeAsync(new CalendarRangeQuery(start.AddMinutes(-1), start.AddDays(5), MaximumOccurrences: 20));
        var matching = occurrences.Where(entry => entry.Id == item.Id).OrderBy(entry => entry.StartAtUtc).ToArray();
        Assert.Equal(4, matching.Length);
        Assert.Equal("Room 5", matching[0].Location);
        Assert.Equal("#34C58A", matching[0].Color);

        var cancelled = await backend.UpsertCalendarEventExceptionAsync(item.Id, matching[1].StartAtUtc, null, null, null, cancelled: true, Request());
        var afterCancel = await backend.GetCalendarEventsRangeAsync(new CalendarRangeQuery(start.AddMinutes(-1), start.AddDays(5), MaximumOccurrences: 20));
        Assert.Equal(3, afterCancel.Count(entry => entry.Id == item.Id));

        var movedStart = matching[1].StartAtUtc.AddHours(4);
        var moved = await backend.UpsertCalendarEventExceptionAsync(item.Id, matching[1].StartAtUtc, movedStart, movedStart.AddMinutes(45), "Moved standup", cancelled: false, Request(cancelled.Revision));
        var afterMove = await backend.GetCalendarEventsRangeAsync(new CalendarRangeQuery(start.AddMinutes(-1), start.AddDays(5), MaximumOccurrences: 20));
        Assert.Contains(afterMove, entry => entry.Id == item.Id && entry.Title == "Moved standup" && entry.StartAtUtc == movedStart && entry.EndAtUtc == movedStart.AddMinutes(45));

        await backend.DeleteCalendarEventExceptionAsync(moved.Id, Request(moved.Revision));
        var afterExceptionDelete = await backend.GetCalendarEventsRangeAsync(new CalendarRangeQuery(start.AddMinutes(-1), start.AddDays(5), MaximumOccurrences: 20));
        Assert.Equal(4, afterExceptionDelete.Count(entry => entry.Id == item.Id));

        var deletedEvent = await backend.DeleteCalendarEventAsync(item.Id, Request(item.Revision));
        Assert.DoesNotContain(await backend.GetCalendarEventsRangeAsync(new CalendarRangeQuery(start.AddMinutes(-1), start.AddDays(5))), entry => entry.Id == item.Id);
        var restoredEvent = await backend.RestoreDeletedCalendarEventAsync(item.Id, Request(deletedEvent.Revision));
        Assert.Null(restoredEvent.DeletedAtUtc);

        var deletedCalendar = await backend.DeleteCalendarAsync(calendar.Id, Request(calendar.Revision));
        Assert.DoesNotContain((await backend.GetBootstrapAsync()).Calendars, entry => entry.Id == calendar.Id);
        var restoredCalendar = await backend.RestoreDeletedCalendarAsync(calendar.Id, Request(deletedCalendar.Revision));
        Assert.Contains((await backend.GetBootstrapAsync()).Calendars, entry => entry.Id == restoredCalendar.Id);

        Directory.Delete(directory.FullName, recursive: true);
    }

    [Fact]
    public async Task HiddenCalendarsAreExcludedFromScheduleAndEventProjections()
    {
        var directory = Directory.CreateTempSubdirectory("snook-calendar-visibility-test-");
        var databasePath = Path.Combine(directory.FullName, "workspace.db");
        await using var store = new SqliteStore(databasePath);
        await using var backend = new SnookBackend(store);
        await backend.InitializeAsync();

        var bootstrap = await backend.GetBootstrapAsync();
        var calendar = Assert.Single(bootstrap.Calendars);
        var activity = Assert.Single(bootstrap.Activities, item => item.DefaultLane == SessionLane.Foreground);
        var start = DateTimeOffset.UtcNow.AddHours(1);
        var block = await backend.CreateScheduleBlockAsync(calendar.Id, null, activity.Id, "Hidden block", start, start.AddMinutes(30), "UTC");
        var item = await backend.CreateCalendarEventAsync(calendar.Id, "Hidden event", start, start.AddMinutes(30));
        var hidden = await backend.UpdateCalendarAsync(calendar.Id, new CalendarUpdate(calendar.Name, calendar.Color, false), Request(calendar.Revision));

        Assert.DoesNotContain(await backend.GetCalendarRangeAsync(new CalendarRangeQuery(start.AddMinutes(-1), start.AddHours(1))), entry => entry.Id == block.Id);
        Assert.DoesNotContain(await backend.GetCalendarEventsRangeAsync(new CalendarRangeQuery(start.AddMinutes(-1), start.AddHours(1))), entry => entry.Id == item.Id);

        await backend.UpdateCalendarAsync(calendar.Id, new CalendarUpdate(calendar.Name, calendar.Color, true), Request(hidden.Revision));
        Assert.Contains(await backend.GetCalendarRangeAsync(new CalendarRangeQuery(start.AddMinutes(-1), start.AddHours(1))), entry => entry.Id == block.Id);
        Assert.Contains(await backend.GetCalendarEventsRangeAsync(new CalendarRangeQuery(start.AddMinutes(-1), start.AddHours(1))), entry => entry.Id == item.Id);

        Directory.Delete(directory.FullName, recursive: true);
    }

    [Fact]
    public async Task DependenciesBlockCompletionAndRejectCycles()
    {
        var directory = Directory.CreateTempSubdirectory("snook-test-");
        var databasePath = Path.Combine(directory.FullName, "workspace.db");
        await using var store = new SqliteStore(databasePath);
        await using var backend = new SnookBackend(store);
        await backend.InitializeAsync();
        var project = Assert.Single((await backend.GetBootstrapAsync()).Projects);
        var prerequisite = await backend.CreateTaskAsync(project.Id, "Review notes");
        var dependent = await backend.CreateTaskAsync(project.Id, "Publish notes");
        await backend.AddTaskDependencyAsync(dependent.Id, prerequisite.Id);

        var blocked = await Assert.ThrowsAsync<SnookException>(() => backend.CompleteTaskAsync(dependent.Id, Request(dependent.Revision)));
        Assert.Equal(SnookErrorCode.DependencyBlocked, blocked.Code);

        var completedPrerequisite = await backend.CompleteTaskAsync(prerequisite.Id, Request(prerequisite.Revision));
        await backend.CompleteTaskAsync(dependent.Id, Request(dependent.Revision));
        await Assert.ThrowsAsync<SnookException>(() => backend.AddTaskDependencyAsync(prerequisite.Id, dependent.Id));
        Assert.Equal(TaskState.Completed, completedPrerequisite.Status);

        Directory.Delete(directory.FullName, recursive: true);
    }

    [Fact]
    public async Task BackupAndExportProduceVerifiableArtifacts()
    {
        var directory = Directory.CreateTempSubdirectory("snook-test-");
        var databasePath = Path.Combine(directory.FullName, "workspace.db");
        await using var store = new SqliteStore(databasePath);
        await using var backend = new SnookBackend(store);
        await backend.InitializeAsync();
        var backup = await backend.CreateBackupAsync(Path.Combine(directory.FullName, "backup.db"));
        var export = await backend.ExportJsonAsync(Path.Combine(directory.FullName, "workspace.json"));
        var csv = await backend.ExportCsvAsync(Path.Combine(directory.FullName, "worklog.csv"), DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        Assert.True(File.Exists(backup.Path));
        Assert.NotNull(backup.ManifestPath);
        Assert.True(File.Exists(backup.ManifestPath));
        Assert.True(File.Exists(export.Path));
        Assert.True(File.Exists(csv.Path));
        Assert.Equal(4, export.SchemaVersion);
        Assert.Equal(64, backup.Sha256.Length);
        Assert.Equal(64, export.Sha256.Length);
        Assert.Equal(64, csv.Sha256.Length);
        Assert.Contains("schemaVersion", await File.ReadAllTextAsync(export.Path));
        Assert.Contains("session_id,interval_id", await File.ReadAllTextAsync(csv.Path));

        var project = Assert.Single((await backend.GetBootstrapAsync()).Projects);
        var createdAfterBackup = await backend.CreateTaskAsync(project.Id, "Transient task");
        var restored = await backend.RestoreBackupAsync(backup.Path);
        var tasksAfterRestore = await backend.SearchTasksAsync(includeCompleted: true);
        Assert.DoesNotContain(tasksAfterRestore, item => item.Task.Id == createdAfterBackup.Id);
        Assert.Equal(backup.Sha256, restored.Sha256);

        Directory.Delete(directory.FullName, recursive: true);
    }

    [Fact]
    public async Task TaskDetailsKeepStableLinksAndCaseInsensitiveTags()
    {
        var directory = Directory.CreateTempSubdirectory("snook-test-");
        var databasePath = Path.Combine(directory.FullName, "workspace.db");
        await using var store = new SqliteStore(databasePath);
        await using var backend = new SnookBackend(store);
        await backend.InitializeAsync();
        var project = Assert.Single((await backend.GetBootstrapAsync()).Projects);
        var task = await backend.CreateTaskAsync(project.Id, "Document the decision");

        var firstTag = await backend.AddTaskTagAsync(task.Id, "Important", "#6767F2");
        var secondTag = await backend.AddTaskTagAsync(task.Id, "important");
        var link = await backend.AddTaskLinkAsync(task.Id, "Decision record", "https://example.test/decision", "url");
        var details = await backend.GetTaskDetailsAsync(task.Id);

        Assert.Equal(firstTag.Id, secondTag.Id);
        Assert.Single(details.Tags);
        Assert.Equal("Important", details.Tags[0].DisplayName);
        Assert.Contains(details.Links, item => item.Id == link.Id && item.Uri.StartsWith("https://", StringComparison.Ordinal));

        Directory.Delete(directory.FullName, recursive: true);
    }

    [Fact]
    public async Task TaskOrganizationMutationsUseRevisionsAndPreserveIdentity()
    {
        var directory = Directory.CreateTempSubdirectory("snook-test-");
        var databasePath = Path.Combine(directory.FullName, "workspace.db");
        await using var store = new SqliteStore(databasePath);
        await using var backend = new SnookBackend(store);
        await backend.InitializeAsync();
        var bootstrap = await backend.GetBootstrapAsync();
        var project = Assert.Single(bootstrap.Projects);
        var activity = Assert.Single(bootstrap.Activities, item => item.DefaultLane == SessionLane.Foreground);
        var task = await backend.CreateTaskAsync(project.Id, "Initial title");

        var updated = await backend.UpdateTaskAsync(task.Id, new TaskUpdate("Updated title", "A durable description", Priority.High, DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(2)), activity.Id, true), Request(task.Revision));
        Assert.Equal(task.Id, updated.Id);
        Assert.Equal("Updated title", updated.Title);
        Assert.True(updated.Starred);

        var moved = await backend.MoveTaskAsync(updated.Id, project.Id, Request(updated.Revision));
        var archived = await backend.ArchiveTaskAsync(moved.Id, Request(moved.Revision));
        Assert.NotNull(archived.ArchivedAtUtc);
        Assert.DoesNotContain(await backend.SearchTasksAsync(), item => item.Task.Id == task.Id);

        var restored = await backend.RestoreTaskAsync(task.Id, Request(archived.Revision));
        Assert.Null(restored.ArchivedAtUtc);
        var deleted = await backend.DeleteTaskAsync(task.Id, Request(restored.Revision));
        Assert.NotNull(deleted.DeletedAtUtc);
        var undeleted = await backend.RestoreDeletedTaskAsync(task.Id, Request(deleted.Revision));
        Assert.Null(undeleted.DeletedAtUtc);
        Assert.Equal(task.Id, (await backend.GetTaskDetailsAsync(task.Id)).Task.Id);

        Directory.Delete(directory.FullName, recursive: true);
    }

    [Fact]
    public async Task RestartMarksUnsafeRunningSessionForExplicitRecovery()
    {
        var directory = Directory.CreateTempSubdirectory("snook-test-");
        var databasePath = Path.Combine(directory.FullName, "workspace.db");
        var store = new SqliteStore(databasePath);
        var backend = new SnookBackend(store);
        await backend.InitializeAsync();
        var bootstrap = await backend.GetBootstrapAsync();
        var activity = Assert.Single(bootstrap.Activities, item => item.DefaultLane == SessionLane.Foreground);
        var started = await backend.StartSessionAsync(null, activity.Id, SessionLane.Foreground, new OperationRequest(Guid.NewGuid(), Guid.NewGuid(), null));
        await backend.DisposeAsync();

        await using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM active_intervals WHERE session_id=$session;";
            command.Parameters.AddWithValue("$session", started.Id.ToString("D"));
            await command.ExecuteNonQueryAsync();
        }

        await using var restartedStore = new SqliteStore(databasePath);
        await using var restartedBackend = new SnookBackend(restartedStore);
        await restartedBackend.InitializeAsync();
        var recovered = Assert.Single((await restartedStore.LoadStateAsync(DateTimeOffset.UtcNow)).Sessions);
        Assert.Equal(SessionState.RecoveryRequired, recovered.State);
        Assert.Equal("missing-open-interval", recovered.RecoveryStatus);

        var stopped = await restartedBackend.ResolveRecoveryAsync(recovered.Id, RecoveryDecision.StopAtLastKnown, Request(recovered.Revision));
        Assert.Equal(SessionState.Stopped, stopped.State);
        Assert.Equal("stop-at-last-known", stopped.RecoveryStatus);
        Assert.Contains("Recovery decision", stopped.Notes, StringComparison.Ordinal);

        Directory.Delete(directory.FullName, recursive: true);
    }

    [Fact]
    public async Task SessionCorrectionPreservesIdentityAndRecordsBeforeAfterProvenance()
    {
        var directory = Directory.CreateTempSubdirectory("snook-test-");
        var databasePath = Path.Combine(directory.FullName, "workspace.db");
        await using var store = new SqliteStore(databasePath);
        await using var backend = new SnookBackend(store);
        await backend.InitializeAsync();
        var bootstrap = await backend.GetBootstrapAsync();
        var project = Assert.Single(bootstrap.Projects);
        var activity = Assert.Single(bootstrap.Activities, item => item.DefaultLane == SessionLane.Foreground);
        var task = await backend.CreateTaskAsync(project.Id, "Corrected task");
        var start = DateTimeOffset.UtcNow.AddHours(-2);
        var end = start.AddMinutes(40);
        var manual = await backend.CreateManualSessionAsync(task.Id, activity.Id, start, end, "Original note");
        var operationId = Guid.NewGuid();
        var correction = new SessionCorrection(
            task.Id,
            activity.Id,
            start.AddMinutes(5),
            end.AddMinutes(-5),
            "Corrected note",
            [new TimeInterval(manual.Intervals[0].Id, start.AddMinutes(5), end.AddMinutes(-5), "manual-correction")],
            "The entry started five minutes later than recorded.");

        var corrected = await backend.CorrectSessionAsync(manual.Id, correction, new OperationRequest(operationId, Guid.NewGuid(), manual.Revision));
        Assert.Equal(manual.Id, corrected.Id);
        Assert.Equal("Corrected note", corrected.Notes);
        Assert.Equal(correction.StartedAtUtc, corrected.StartedAtUtc);
        Assert.Equal(correction.StoppedAtUtc, corrected.StoppedAtUtc);

        var audit = Assert.Single(await backend.GetSessionCorrectionsAsync(manual.Id));
        Assert.Equal(operationId, audit.OperationId);
        Assert.Equal("The entry started five minutes later than recorded.", audit.Reason);
        Assert.Equal("Original note", audit.Before.Notes);
        Assert.Equal("Corrected note", audit.After.Notes);
        Assert.Equal(manual.Id, audit.Before.Id);
        Assert.Equal(manual.Id, audit.After.Id);

        var retry = await backend.CorrectSessionAsync(manual.Id, correction, new OperationRequest(operationId, Guid.NewGuid(), manual.Revision));
        Assert.Equal(corrected.Revision, retry.Revision);
        Assert.Single(await backend.GetSessionCorrectionsAsync(manual.Id));

        Directory.Delete(directory.FullName, recursive: true);
    }

    [Fact]
    public async Task OrganizationNamesAreScopedAndLifecycleMutationsAreRevisionChecked()
    {
        var directory = Directory.CreateTempSubdirectory("snook-test-");
        var databasePath = Path.Combine(directory.FullName, "workspace.db");
        await using var store = new SqliteStore(databasePath);
        await using var backend = new SnookBackend(store);
        await backend.InitializeAsync();
        var initial = await backend.GetBootstrapAsync();
        var board = await backend.CreateBoardAsync("Delivery");
        var project = await backend.CreateProjectAsync(board.Id, "Launch", "Release planning", starred: true);
        var activity = await backend.CreateActivityAsync("Research", "Background reading", SessionLane.Background);

        var renamedProject = await backend.UpdateProjectAsync(project.Id, new ProjectUpdate("Launch plan", "Release planning", true), Request(project.Revision));
        var renamedActivity = await backend.UpdateActivityAsync(activity.Id, new ActivityUpdate("Research and notes", "Background reading", SessionLane.Background), Request(activity.Revision));
        var archivedBoard = await backend.ArchiveBoardAsync(board.Id, Request(board.Revision));
        var restoredBoard = await backend.RestoreBoardAsync(board.Id, Request(archivedBoard.Revision));
        var archivedProject = await backend.ArchiveProjectAsync(renamedProject.Id, Request(renamedProject.Revision));
        var restoredProject = await backend.RestoreProjectAsync(renamedProject.Id, Request(archivedProject.Revision));
        var archivedActivity = await backend.ArchiveActivityAsync(renamedActivity.Id, Request(renamedActivity.Revision));
        var restoredActivity = await backend.RestoreActivityAsync(renamedActivity.Id, Request(archivedActivity.Revision));

        Assert.Equal("Launch plan", restoredProject.Name);
        Assert.Equal("Research and notes", restoredActivity.Name);
        Assert.Null(restoredBoard.ArchivedAtUtc);
        Assert.Null(restoredProject.ArchivedAtUtc);
        Assert.Null(restoredActivity.ArchivedAtUtc);
        Assert.Contains((await backend.GetBootstrapAsync()).Projects, item => item.Id == project.Id);
        Assert.Contains((await backend.GetBootstrapAsync()).Activities, item => item.Id == activity.Id);
        Assert.NotEqual(initial.Projects[0].Id, project.Id);

        var duplicate = await Assert.ThrowsAsync<SnookException>(() => backend.CreateProjectAsync(board.Id, "Launch plan"));
        Assert.Equal(SnookErrorCode.ValidationFailed, duplicate.Code);

        Directory.Delete(directory.FullName, recursive: true);
    }

    [Fact]
    public async Task OrganizationSoftDeleteHidesItemsAndRestoresThemWithRevisions()
    {
        var directory = Directory.CreateTempSubdirectory("snook-test-");
        var databasePath = Path.Combine(directory.FullName, "workspace.db");
        await using var store = new SqliteStore(databasePath);
        await using var backend = new SnookBackend(store);
        await backend.InitializeAsync();

        var board = await backend.CreateBoardAsync("Archive board");
        var project = await backend.CreateProjectAsync(board.Id, "Archive project");
        var activity = await backend.CreateActivityAsync("Archive activity");

        var deletedProject = await backend.DeleteProjectAsync(project.Id, Request(project.Revision));
        var deletedBoard = await backend.DeleteBoardAsync(board.Id, Request(board.Revision));
        var deletedActivity = await backend.DeleteActivityAsync(activity.Id, Request(activity.Revision));
        var hidden = await backend.GetBootstrapAsync();

        Assert.DoesNotContain(hidden.Boards, item => item.Id == board.Id);
        Assert.DoesNotContain(hidden.Projects, item => item.Id == project.Id);
        Assert.DoesNotContain(hidden.Activities, item => item.Id == activity.Id);

        var restoredBoard = await backend.RestoreDeletedBoardAsync(board.Id, Request(deletedBoard.Revision));
        var restoredProject = await backend.RestoreDeletedProjectAsync(project.Id, Request(deletedProject.Revision));
        var restoredActivity = await backend.RestoreDeletedActivityAsync(activity.Id, Request(deletedActivity.Revision));
        var visible = await backend.GetBootstrapAsync();

        Assert.Equal(board.Id, restoredBoard.Id);
        Assert.Equal(project.Id, restoredProject.Id);
        Assert.Equal(activity.Id, restoredActivity.Id);
        Assert.Contains(visible.Boards, item => item.Id == board.Id);
        Assert.Contains(visible.Projects, item => item.Id == project.Id);
        Assert.Contains(visible.Activities, item => item.Id == activity.Id);

        Directory.Delete(directory.FullName, recursive: true);
    }

    [Fact]
    public async Task CalendarEventSchemaMigrationAdvancesExistingRevisionThreeDatabase()
    {
        var directory = Directory.CreateTempSubdirectory("snook-test-");
        var databasePath = Path.Combine(directory.FullName, "workspace.db");
        await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE schema_migrations(sequence INTEGER PRIMARY KEY,name TEXT NOT NULL,checksum TEXT NOT NULL,applied_at_utc_ms INTEGER NOT NULL); INSERT INTO schema_migrations(sequence,name,checksum,applied_at_utc_ms) VALUES(3,'tracking-corrections','legacy',0);";
            await command.ExecuteNonQueryAsync();
        }

        await using var store = new SqliteStore(databasePath);
        await store.InitializeAsync();
        var state = await store.LoadStateAsync(DateTimeOffset.UtcNow);
        Assert.NotEmpty(state.Calendars);
        Assert.Empty(state.CalendarEvents);

        await using var verification = new SqliteConnection($"Data Source={databasePath}");
        await verification.OpenAsync();
        await using var query = verification.CreateCommand();
        query.CommandText = "SELECT MAX(sequence) FROM schema_migrations;";
        Assert.Equal(9L, Convert.ToInt64(await query.ExecuteScalarAsync(), CultureInfo.InvariantCulture));

        Directory.Delete(directory.FullName, recursive: true);
    }

    [Fact]
    public async Task ActivityGroupMigrationAddsColumnToAnExistingActivitiesTable()
    {
        var directory = Directory.CreateTempSubdirectory("snook-test-");
        var databasePath = Path.Combine(directory.FullName, "workspace.db");
        await using (var initialStore = new SqliteStore(databasePath))
        {
            await initialStore.InitializeAsync();
        }

        await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE journal_entry_tags; DROP TABLE journal_entries; DROP TABLE journals; DROP TABLE habit_check_ins; DROP TABLE habits; ALTER TABLE activities DROP COLUMN group_id; DELETE FROM schema_migrations; INSERT INTO schema_migrations(sequence,name,checksum,applied_at_utc_ms) VALUES(5,'workspace-settings','legacy',0);";
            await command.ExecuteNonQueryAsync();
        }

        await using (var migratedStore = new SqliteStore(databasePath))
        {
            await migratedStore.InitializeAsync();
            var state = await migratedStore.LoadStateAsync(DateTimeOffset.UtcNow);
            Assert.NotEmpty(state.Activities);

            await using var verification = new SqliteConnection($"Data Source={databasePath}");
            await verification.OpenAsync();
            await using var query = verification.CreateCommand();
            query.CommandText = "SELECT COUNT(*) FROM pragma_table_info('activities') WHERE name='group_id';";
            Assert.Equal(1L, Convert.ToInt64(await query.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
        }

        Directory.Delete(directory.FullName, recursive: true);
    }

    [Fact]
    public async Task WorkspaceLeaseRejectsASecondHostUntilTheOwnerReleasesIt()
    {
        var directory = Directory.CreateTempSubdirectory("snook-lease-test-");
        var databasePath = Path.Combine(directory.FullName, "workspace.db");
        var owner = new SqliteStore(databasePath);
        var contender = new SqliteStore(databasePath);
        var ownerDisposed = false;
        try
        {
            await owner.InitializeAsync();

            var conflict = await Assert.ThrowsAsync<SnookException>(() => contender.InitializeAsync());
            Assert.Equal(SnookErrorCode.StoreUnavailable, conflict.Code);

            await owner.DisposeAsync();
            ownerDisposed = true;
            await contender.InitializeAsync();
        }
        finally
        {
            await contender.DisposeAsync();
            if (!ownerDisposed)
            {
                await owner.DisposeAsync();
            }

            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task StaleWorkspaceLeaseIsRecoveredWhenItsOwnerProcessIsGone()
    {
        var directory = Directory.CreateTempSubdirectory("snook-stale-lease-test-");
        var databasePath = Path.Combine(directory.FullName, "workspace.db");
        var leasePath = databasePath + ".owner";
        await File.WriteAllTextAsync(leasePath, $"pid={int.MaxValue}{Environment.NewLine}started={DateTimeOffset.UtcNow:O}");

        await using var store = new SqliteStore(databasePath);
        await store.InitializeAsync();

        Assert.NotEmpty((await store.LoadStateAsync(DateTimeOffset.UtcNow)).Boards);
        Directory.Delete(directory.FullName, recursive: true);
    }

    [Fact]
    public async Task FailedStartupReleasesTheLeaseWithoutResettingTheDatabase()
    {
        var directory = Directory.CreateTempSubdirectory("snook-startup-failure-test-");
        var databasePath = Path.Combine(directory.FullName, "workspace.db");
        await using (var initialStore = new SqliteStore(databasePath))
        {
            await initialStore.InitializeAsync();
        }

        string checksum;
        await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            await connection.OpenAsync();
            await using var read = connection.CreateCommand();
            read.CommandText = "SELECT checksum FROM schema_migrations ORDER BY sequence DESC LIMIT 1;";
            var scalar = await read.ExecuteScalarAsync();
            Assert.NotNull(scalar);
            checksum = Convert.ToString(scalar, CultureInfo.InvariantCulture)!;
            await using var corrupt = connection.CreateCommand();
            corrupt.CommandText = "UPDATE schema_migrations SET checksum='invalid' WHERE sequence=(SELECT MAX(sequence) FROM schema_migrations);";
            await corrupt.ExecuteNonQueryAsync();
        }

        var failedStore = new SqliteStore(databasePath);
        var failure = await Assert.ThrowsAsync<SnookException>(() => failedStore.InitializeAsync());
        Assert.Equal(SnookErrorCode.SchemaIncompatible, failure.Code);
        await failedStore.DisposeAsync();

        await using (var repair = new SqliteConnection($"Data Source={databasePath}"))
        {
            await repair.OpenAsync();
            await using var command = repair.CreateCommand();
            command.CommandText = "UPDATE schema_migrations SET checksum=$checksum WHERE sequence=(SELECT MAX(sequence) FROM schema_migrations);";
            command.Parameters.AddWithValue("$checksum", checksum);
            await command.ExecuteNonQueryAsync();
        }

        await using var retryStore = new SqliteStore(databasePath);
        await retryStore.InitializeAsync();
        Assert.NotEmpty((await retryStore.LoadStateAsync(DateTimeOffset.UtcNow)).Boards);
        Directory.Delete(directory.FullName, recursive: true);
    }

    [Fact]
    public async Task ForegroundConcurrencySettingIsPersistedAndControlsNewSessions()
    {
        var directory = Directory.CreateTempSubdirectory("snook-test-");
        var databasePath = Path.Combine(directory.FullName, "workspace.db");
        await using var store = new SqliteStore(databasePath);
        await using var backend = new SnookBackend(store);
        await backend.InitializeAsync();
        var bootstrap = await backend.GetBootstrapAsync();
        var project = Assert.Single(bootstrap.Projects);
        var activity = Assert.Single(bootstrap.Activities, item => item.DefaultLane == SessionLane.Foreground);
        var firstTask = await backend.CreateTaskAsync(project.Id, "First focus");
        var secondTask = await backend.CreateTaskAsync(project.Id, "Second focus");

        var first = await backend.StartSessionAsync(firstTask.Id, activity.Id, SessionLane.Foreground, Request());
        var updatedSettings = await backend.UpdateSettingsAsync(
            new WorkspaceSettings(true, bootstrap.Settings!.Revision),
            Request(bootstrap.Settings.Revision));
        var second = await backend.StartSessionAsync(secondTask.Id, activity.Id, SessionLane.Foreground, Request());
        Assert.Equal(SessionState.Running, first.State);
        Assert.Equal(SessionState.Running, second.State);

        var disabled = await backend.UpdateSettingsAsync(new WorkspaceSettings(false, updatedSettings.Revision), Request(updatedSettings.Revision));
        var thirdTask = await backend.CreateTaskAsync(project.Id, "Third focus");
        var third = await backend.StartSessionAsync(thirdTask.Id, activity.Id, SessionLane.Foreground, Request());
        var refreshed = await backend.GetBootstrapAsync();
        Assert.False(disabled.AllowConcurrentForeground);
        Assert.Equal(SessionState.Running, third.State);
        Assert.Contains(refreshed.Today.ActiveSessions, item => item.Session.Id == third.Id && item.Session.State == SessionState.Running);
        Assert.Contains(refreshed.Today.ActiveSessions, item => item.Session.Id == first.Id && item.Session.State == SessionState.Paused);
        Assert.Contains(refreshed.Today.ActiveSessions, item => item.Session.Id == second.Id && item.Session.State == SessionState.Paused);

        Directory.Delete(directory.FullName, recursive: true);
    }

    [Fact]
    public async Task DaemonClientUsesAuthenticatedRpcAndReceivesCommittedChanges()
    {
        var directory = Directory.CreateTempSubdirectory("snook-daemon-test-");
        using var portProbe = new TcpListener(IPAddress.Loopback, 0);
        portProbe.Start();
        var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
        portProbe.Stop();

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.StartInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "snookd.dll"));
        process.StartInfo.Environment["SNOOK_DATA_DIR"] = directory.FullName;
        process.StartInfo.Environment["SNOOK_DAEMON_PORT"] = port.ToString(CultureInfo.InvariantCulture);
        Assert.True(process.Start());

        var readiness = await ReadDaemonReadinessAsync(process.StandardOutput, process.StandardError);
        Assert.True(readiness.Ready);
        Assert.Equal($"1.{ContractInfo.Minor}", readiness.Contract);
        var tokenPath = Path.Combine(directory.FullName, "Snook", "daemon.token");
        var token = await WaitForFileTextAsync(tokenPath);
        var endpoint = new Uri($"http://127.0.0.1:{port}/");

        using var unauthenticated = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var healthResponse = await WaitForResponseAsync(unauthenticated, new Uri(endpoint, "v1/health"));
        Assert.Equal(HttpStatusCode.Unauthorized, healthResponse.StatusCode);

        using var oversizedRequest = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, "v1/call"));
        oversizedRequest.Headers.Add("X-Snook-Token", token);
        oversizedRequest.Content = new StringContent(new string('x', 1_100_000));
        using var oversizedResponse = await WaitForResponseAsync(unauthenticated, oversizedRequest);
        Assert.Equal(HttpStatusCode.BadRequest, oversizedResponse.StatusCode);

        await using var client = new DaemonBackendClient(endpoint, token);
        var bootstrap = await client.GetBootstrapAsync();
        Assert.True(bootstrap.Capabilities.DaemonClient);
        Assert.False(bootstrap.Capabilities.Embedded);
        Assert.Equal("daemon-client", bootstrap.Capabilities.HostMode);

        var change = new TaskCompletionSource<ChangeNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Changed += (_, notification) =>
        {
            if (notification.AggregateType == "board" && notification.ChangeKind == "created")
            {
                change.TrySetResult(notification);
            }
        };

        var created = await client.CreateBoardAsync("Daemon contract board");
        var notification = await change.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(created.Id, notification.AggregateId);
        Assert.True(notification.Cursor > bootstrap.CommittedCursor);

        // The dispatcher derives its allowlist from IBackendClient.
        try
        {
            await TaskBatchTests.AssertBatchContractAsync(client);
            await HabitTests.AssertHabitContractAsync(client);
            await JournalTests.AssertJournalContractAsync(client);
        }
        finally
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    private static async Task<(bool Ready, string Contract)> ReadDaemonReadinessAsync(StreamReader output, StreamReader error)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            var line = await output.ReadLineAsync(timeout.Token);
            if (line is null)
            {
                var diagnostics = await error.ReadToEndAsync(timeout.Token);
                throw new InvalidOperationException($"snookd exited before readiness: {diagnostics}");
            }

            using var document = JsonDocument.Parse(line);
            if (document.RootElement.TryGetProperty("ready", out var ready) && ready.GetBoolean())
            {
                return (
                    true,
                    document.RootElement.GetProperty("contract").GetString() ?? string.Empty);
            }
        }
    }

    private static async Task<string> WaitForFileTextAsync(string path)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!File.Exists(path))
        {
            await Task.Delay(50, timeout.Token);
        }

        return (await File.ReadAllTextAsync(path, timeout.Token)).Trim();
    }

    private static async Task<HttpResponseMessage> WaitForResponseAsync(HttpClient client, Uri uri)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            }
            catch (HttpRequestException) when (!timeout.IsCancellationRequested)
            {
                await Task.Delay(50, timeout.Token);
            }
        }
    }

    private static async Task<HttpResponseMessage> WaitForResponseAsync(HttpClient client, HttpRequestMessage request)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            try
            {
                return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            }
            catch (HttpRequestException) when (!timeout.IsCancellationRequested)
            {
                await Task.Delay(50, timeout.Token);
            }
        }
    }

    private static async Task<IReadOnlyList<DomainCalendar>> ReadCalendarsAsync(SqliteStore store)
    {
        var state = await store.LoadStateAsync(DateTimeOffset.UtcNow);
        return state.Calendars;
    }

    private static OperationRequest Request(long? revision = null) => new(Guid.NewGuid(), Guid.NewGuid(), revision);
}
