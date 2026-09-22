using System.Globalization;
using Snook.Application;
using Snook.Contracts;
using Snook.Domain;

namespace Snook.UI;

public sealed partial class MainWindowViewModel
{
    private ManualRecoveryArguments? _manualRecoveryArguments;
    private PlanRecoveryArguments? _planRecoveryArguments;
    private EventRecoveryArguments? _eventRecoveryArguments;

    public Func<Task>? UtilityEditorCheckpointAsync { get; set; }

    internal Task CheckpointUtilityEditorAsync()
        => UtilityEditorCheckpointAsync?.Invoke() ?? Task.CompletedTask;

    public UtilityEditorRecoveryDraft? CaptureUtilityEditorDraft(Guid workspaceId)
    {
        if (!IsUtilityEditorOpen || IsTaskEditorOpen || workspaceId == Guid.Empty) return null;
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        Guid? entityId = null;
        long? revision = null;
        IReadOnlyList<TaskRevision> targets = [];
        OperationRequest? request = null;
        var kind = _utilityKind!;

        switch (kind)
        {
            case "manual":
            {
                values["task"] = S(ManualTaskId);
                values["activity"] = S(ManualActivityId);
                values["start"] = ManualStartText;
                values["end"] = ManualEndText;
                values["notes"] = ManualNotes;
                var intent = new { TaskId = ManualTaskId, ActivityId = ManualActivityId,
                    Notes = string.IsNullOrWhiteSpace(ManualNotes) ? null : ManualNotes.Trim(),
                    StartText = ManualStartText, EndText = ManualEndText };
                request = _manualOperation.RequestFor(intent);
                if (request is not null && _manualRecoveryArguments is { } arguments)
                {
                    values["capturedStart"] = O(arguments.Start);
                    values["capturedEnd"] = O(arguments.End);
                }
                break;
            }
            case "activity":
            {
                values["name"] = NewActivityName;
                values["description"] = NewActivityDescription;
                values["lane"] = I((int)NewActivityLane);
                values["group"] = S(NewActivityGroupId == Guid.Empty ? null : NewActivityGroupId);
                var intent = new { Name = NewActivityName.Trim(), Description = NewActivityDescription.Trim(),
                    Lane = NewActivityLane, GroupId = NewActivityGroupId == Guid.Empty ? (Guid?)null : NewActivityGroupId };
                request = _createActivityOperation.RequestFor(intent);
                break;
            }
            case "plan":
            {
                values["calendar"] = S(SelectedCalendarId);
                values["task"] = S(CalendarPlanTaskId);
                values["start"] = CalendarPlanStartText;
                values["end"] = CalendarPlanEndText;
                var intent = new { CalendarId = SelectedCalendarId, TaskId = CalendarPlanTaskId,
                    StartText = CalendarPlanStartText, EndText = CalendarPlanEndText };
                request = _planOperation.RequestFor(intent);
                if (request is not null && _planRecoveryArguments is { } arguments)
                {
                    values["capturedStart"] = O(arguments.Start);
                    values["capturedEnd"] = O(arguments.End);
                    values["capturedZone"] = arguments.TimeZone;
                    values["capturedActivity"] = S(arguments.ActivityId);
                    values["capturedTitle"] = arguments.Title;
                }
                break;
            }
            case "event":
            {
                CaptureEventCreation(values);
                request = _createEventOperation.RequestFor(EventCreationIntent());
                if (request is not null && _eventRecoveryArguments is { } arguments)
                {
                    values["capturedStart"] = O(arguments.Start);
                    values["capturedEnd"] = O(arguments.End);
                    values["capturedZone"] = arguments.TimeZone;
                    values["capturedRecurrenceEnd"] = arguments.RecurrenceEnd is { } end ? O(end) : null;
                }
                break;
            }
            case "edit" when UtilityDraft is BoardAdminRowViewModel row:
                kind = "board"; entityId = row.Board.Id; revision = row.Board.Revision;
                values["name"] = row.DraftName; break;
            case "edit" when UtilityDraft is ProjectAdminRowViewModel row:
                kind = "project"; entityId = row.Project.Id; revision = row.Project.Revision;
                values["name"] = row.DraftName; values["starred"] = B(row.DraftStarred);
                values["tag"] = row.TagText; break;
            case "edit" when UtilityDraft is ActivityGroupAdminRowViewModel row:
                kind = "activity-group"; entityId = row.Group.Id; revision = row.Group.Revision;
                values["name"] = row.DraftName; break;
            case "edit" when UtilityDraft is ActivityAdminRowViewModel row:
                kind = "activity-edit"; entityId = row.Activity.Id; revision = row.Activity.Revision;
                values["name"] = row.DraftName; values["description"] = row.DraftDescription;
                values["lane"] = I((int)row.DraftLane); values["group"] = S(row.GroupId);
                values["tag"] = row.TagText; break;
            case "edit" when UtilityDraft is CalendarAdminRowViewModel row:
                kind = "calendar"; entityId = row.Calendar.Id; revision = row.Calendar.Revision;
                values["name"] = row.DraftName; values["color"] = row.DraftColor;
                values["visible"] = B(row.IsVisible); break;
            case "edit" when UtilityDraft is HistoryRowViewModel row:
                kind = "history"; entityId = row.Item.Session.Id; revision = row.Item.Session.Revision;
                values["anchor"] = row.Item.Session.StartedAtUtc.ToString("O", CultureInfo.InvariantCulture);
                values["task"] = S(row.SelectedTaskId); values["activity"] = S(row.SelectedActivityId);
                values["notes"] = row.NotesEditor; values["start"] = row.StartText;
                values["end"] = row.EndText; values["reason"] = row.ReasonText; break;
            case "edit" when UtilityDraft is CalendarBlockRowViewModel row:
                kind = "block"; entityId = row.Block.Id; revision = row.Block.Revision;
                values["anchor"] = row.Block.StartAtUtc.ToString("O", CultureInfo.InvariantCulture);
                values["title"] = row.TitleEditor; values["start"] = row.StartText;
                values["end"] = row.EndText; break;
            case "edit" when UtilityDraft is CalendarEventRowViewModel row:
                kind = "event-edit"; entityId = row.Event.Id; revision = row.Event.Revision;
                values["anchor"] = row.Event.StartAtUtc.ToString("O", CultureInfo.InvariantCulture);
                CaptureEventEdit(values, row); break;
            case "journal" when UtilityDraft is JournalEditorViewModel row:
                entityId = row.Journal?.Id; revision = row.Journal?.Revision;
                values["name"] = row.Name; values["description"] = row.Description;
                request = row.Operation.RequestFor(new JournalDefinition(row.Name, row.Description), revision);
                break;
            case "journal-entry" when UtilityDraft is JournalEntryEditorViewModel row && row.CanEdit:
                entityId = row.Entry?.Id; revision = row.Entry?.Revision;
                values["journal"] = S(row.JournalId); values["title"] = row.Title;
                values["content"] = row.Content; values["date"] = row.DateText;
                values["tags"] = row.TagsText; values["mood"] = I(row.MoodRating);
                try
                {
                    var definition = row.Definition();
                    request = row.Operation.RequestFor(definition, revision);
                    if (request is not null) values["capturedOccurred"] = O(definition.OccurredAtUtc);
                }
                catch (SnookException) { }
                break;
            case "habit" when UtilityDraft is HabitEditorViewModel row:
                entityId = row.Habit?.Id; revision = row.Habit?.Revision;
                values["name"] = row.Name; values["description"] = row.Description;
                values["start"] = row.StartDateText; values["zone"] = row.CreationTimeZone;
                try
                {
                    object operation = row.Habit is { }
                        ? new HabitUpdate(row.Name, row.Description)
                        : new HabitDefinition(row.Name, row.Description,
                            DateOnly.ParseExact(row.StartDateText, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                            row.CreationTimeZone);
                    request = row.RequestFor(operation, revision);
                }
                catch (FormatException) { }
                break;
            case "bulk" when UtilityDraft is BulkTaskEditorViewModel row:
                targets = row.Targets.Select(task => new TaskRevision(task.Id, task.Revision)).ToArray();
                CaptureBulk(values, row);
                try { request = row.RequestForCurrent(); }
                catch (Exception exception) when (exception is FormatException or ArgumentException) { }
                break;
            default:
                return null;
        }

        return new UtilityEditorRecoveryDraft(1, workspaceId, DateTimeOffset.UtcNow,
            kind, entityId, revision, values, targets, request);
    }

    public async Task<bool> RestoreUtilityEditorDraftAsync(UtilityEditorRecoveryDraft draft,
        Guid workspaceId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (!draft.IsValid(workspaceId, DateTimeOffset.UtcNow) || IsTaskEditorOpen
            || IsUtilityEditorOpen) return false;
        var v = draft.Values;
        switch (draft.Kind)
        {
            case "manual": OpenUtilityEditor("manual"); break;
            case "activity": OpenUtilityEditor("activity"); break;
            case "plan": OpenUtilityEditor("plan"); break;
            case "event": OpenUtilityEditor("event"); break;
            case "board":
            {
                var current = BoardAdminRows.FirstOrDefault(row => row.Board.Id == draft.EntityId);
                if (current is null) return false;
                OpenUtilityEditor(new BoardAdminRowViewModel(current.Board with { Revision = draft.ExpectedRevision!.Value },
                    SaveBoardAsync, ToggleBoardArchiveAsync, DeleteBoardAsync, ReorderBoardAsync));
                break;
            }
            case "project":
            {
                var current = ProjectAdminRows.FirstOrDefault(row => row.Project.Id == draft.EntityId);
                if (current is null) return false;
                OpenUtilityEditor(new ProjectAdminRowViewModel(current.Project with { Revision = draft.ExpectedRevision!.Value },
                    SaveProjectAsync, ToggleProjectArchiveAsync, DeleteProjectAsync, AddProjectTagAsync, ReorderProjectAsync));
                break;
            }
            case "activity-group":
            {
                var current = ActivityGroupAdminRows.FirstOrDefault(row => row.Group.Id == draft.EntityId);
                if (current is null) return false;
                OpenUtilityEditor(new ActivityGroupAdminRowViewModel(current.Group with { Revision = draft.ExpectedRevision!.Value },
                    SaveActivityGroupAsync, DeleteActivityGroupAsync, ReorderActivityGroupAsync));
                break;
            }
            case "activity-edit":
            {
                var current = ActivityAdminRows.FirstOrDefault(row => row.Activity.Id == draft.EntityId);
                if (current is null) return false;
                OpenUtilityEditor(new ActivityAdminRowViewModel(current.Activity with { Revision = draft.ExpectedRevision!.Value },
                    ActivityGroups.ToArray(), SaveActivityAsync, ToggleActivityArchiveAsync,
                    DeleteActivityAsync, AddActivityTagAsync, StartActivityAsync));
                break;
            }
            case "calendar":
            {
                var current = CalendarAdminRows.FirstOrDefault(row => row.Calendar.Id == draft.EntityId);
                if (current is null) return false;
                OpenUtilityEditor(new CalendarAdminRowViewModel(current.Calendar with { Revision = draft.ExpectedRevision!.Value },
                    SaveCalendarAsync, DeleteCalendarAsync));
                break;
            }
            case "history":
            {
                var item = await FindHistoryItemAsync(draft.EntityId!.Value, Date(v, "anchor"), cancellationToken);
                if (item is null) return false;
                OpenUtilityEditor(new HistoryRowViewModel(item with
                    { Session = item.Session with { Revision = draft.ExpectedRevision!.Value } },
                    Tasks.Select(task => new TaskOption(task.Task.Id, task.Title)).ToArray(),
                    Activities.ToArray(), CorrectHistoryAsync));
                break;
            }
            case "block":
            {
                var anchor = Date(v, "anchor");
                var block = (await _backend.GetCalendarRangeAsync(new CalendarRangeQuery(
                    anchor.AddDays(-2), anchor.AddDays(2)), cancellationToken: cancellationToken))
                    .FirstOrDefault(item => item.Id == draft.EntityId);
                if (block is null) return false;
                OpenUtilityEditor(new CalendarBlockRowViewModel(block with { Revision = draft.ExpectedRevision!.Value },
                    V(v, "title"), UpdateCalendarBlockAsync, StartCalendarBlockAsync));
                break;
            }
            case "event-edit":
            {
                var anchor = Date(v, "anchor");
                var item = (await _backend.GetCalendarEventsRangeAsync(new CalendarRangeQuery(
                    anchor.AddDays(-2), anchor.AddDays(2)), cancellationToken: cancellationToken))
                    .FirstOrDefault(value => value.Id == draft.EntityId);
                if (item is null) return false;
                OpenUtilityEditor(new CalendarEventRowViewModel(item with { Revision = draft.ExpectedRevision!.Value },
                    UpdateCalendarEventAsync, DeleteCalendarEventAsync));
                break;
            }
            case "journal":
            {
                Journal? journal = null;
                if (draft.EntityId is { } id)
                {
                    journal = _journals.FirstOrDefault(item => item.Id == id);
                    if (journal is null) return false;
                    journal = journal with { Revision = draft.ExpectedRevision!.Value };
                }
                OpenJournalEditor(journal);
                break;
            }
            case "journal-entry":
            {
                JournalEntry? entry = null;
                if (draft.EntityId is { } id)
                {
                    entry = await _backend.GetJournalEntryAsync(id, cancellationToken);
                    entry = entry with { Revision = draft.ExpectedRevision!.Value };
                }
                var editor = new JournalEntryEditorViewModel(entry, _journals, GuidV(v, "journal") ?? Guid.Empty,
                    readOnly: false, SaveJournalEntryAsync, ReviewJournalEntryAsync);
                PresentRecoveredUtility(editor, entry is null ? "A moment to reflect" : "Your journal entry",
                    "journal-entry", editor.SaveCommand);
                break;
            }
            case "habit":
            {
                Habit? habit = null;
                if (draft.EntityId is { } id)
                {
                    habit = (await _backend.GetHabitsAsync(new HabitQuery(1, IncludeArchived: true,
                        IncludeDeleted: true), cancellationToken)).FirstOrDefault(item => item.Habit.Id == id)?.Habit;
                    if (habit is null) return false;
                    habit = habit with { Revision = draft.ExpectedRevision!.Value };
                }
                var creationZone = string.IsNullOrWhiteSpace(V(v, "zone")) ? null : V(v, "zone");
                var editor = new HabitEditorViewModel(habit, SaveHabitAsync, ReviewHabitAsync,
                    creationZone);
                PresentRecoveredUtility(editor, habit is null ? "New daily habit" : "Edit daily habit",
                    "habit", editor.SaveCommand);
                break;
            }
            case "bulk":
            {
                var all = await _backend.SearchTasksAsync(includeCompleted: true, includeArchived: true,
                    includeDeleted: true, cancellationToken: cancellationToken);
                var byId = all.Select(item => item.Task).ToDictionary(task => task.Id);
                if (draft.TargetRevisions.Any(target => !byId.ContainsKey(target.TaskId))) return false;
                var editor = new BulkTaskEditorViewModel(draft.TargetRevisions.Select(target =>
                        byId[target.TaskId] with { Revision = target.ExpectedRevision }).ToArray(),
                    Projects.ToArray(), Activities.ToArray(), SaveTaskBatchAsync, ReviewTaskBatchAsync);
                PresentRecoveredUtility(editor, $"Edit {draft.TargetRevisions.Count} tasks", "bulk", editor.SaveCommand);
                break;
            }
            default: return false;
        }

        ApplyRecoveredValues(draft);
        RestorePendingUtilityRequest(draft);
        StatusMessage = draft.PendingRequest is null
            ? "Unsaved editor recovered. Review it before saving."
            : "Editor recovered with an unconfirmed save. Retry unchanged to confirm it safely.";
        return true;
    }

    private async Task<HistoryItem?> FindHistoryItemAsync(Guid id, DateTimeOffset anchor,
        CancellationToken cancellationToken)
    {
        string? continuation = null;
        for (var pageIndex = 0; pageIndex < 5; pageIndex++)
        {
            var page = await _backend.GetHistoryAsync(new HistoryQuery(anchor.AddDays(-2),
                anchor.AddDays(2), PageSize: 100, ContinuationToken: continuation), cancellationToken);
            var found = page.Items.FirstOrDefault(item => item.Session.Id == id);
            if (found is not null || page.ContinuationToken is null) return found;
            continuation = page.ContinuationToken;
        }
        return null;
    }

    private void PresentRecoveredUtility(object editor, string title, string kind,
        System.Windows.Input.ICommand command)
    {
        UtilityDraft = editor;
        UtilityTitle = title;
        UtilitySaveCommand = command;
        _utilityKind = kind;
        NotifyUtilityEditor();
    }

    private void ApplyRecoveredValues(UtilityEditorRecoveryDraft draft)
    {
        var v = draft.Values;
        switch (draft.Kind)
        {
            case "manual":
                ManualTaskId = GuidV(v, "task"); ManualActivityId = GuidV(v, "activity");
                ManualStartText = V(v, "start"); ManualEndText = V(v, "end"); ManualNotes = V(v, "notes"); break;
            case "activity":
                NewActivityName = V(v, "name"); NewActivityDescription = V(v, "description");
                NewActivityLane = EnumV<SessionLane>(v, "lane"); NewActivityGroupId = GuidV(v, "group") ?? Guid.Empty; break;
            case "plan":
                SelectedCalendarId = GuidV(v, "calendar") ?? Guid.Empty; CalendarPlanTaskId = GuidV(v, "task");
                CalendarPlanStartText = V(v, "start"); CalendarPlanEndText = V(v, "end"); break;
            case "event": ApplyEventCreation(v); break;
            case "board" when UtilityDraft is BoardAdminRowViewModel row: row.DraftName = V(v, "name"); break;
            case "project" when UtilityDraft is ProjectAdminRowViewModel row:
                row.DraftName = V(v, "name"); row.DraftStarred = BoolV(v, "starred"); row.TagText = V(v, "tag"); break;
            case "activity-group" when UtilityDraft is ActivityGroupAdminRowViewModel row: row.DraftName = V(v, "name"); break;
            case "activity-edit" when UtilityDraft is ActivityAdminRowViewModel row:
                row.DraftName = V(v, "name"); row.DraftDescription = V(v, "description");
                row.DraftLane = EnumV<SessionLane>(v, "lane"); row.GroupId = GuidV(v, "group") ?? Guid.Empty;
                row.TagText = V(v, "tag"); break;
            case "calendar" when UtilityDraft is CalendarAdminRowViewModel row:
                row.DraftName = V(v, "name"); row.DraftColor = V(v, "color"); row.IsVisible = BoolV(v, "visible"); break;
            case "history" when UtilityDraft is HistoryRowViewModel row:
                row.SelectedTaskId = GuidV(v, "task"); row.SelectedActivityId = GuidV(v, "activity");
                row.NotesEditor = V(v, "notes"); row.StartText = V(v, "start"); row.EndText = V(v, "end");
                row.ReasonText = V(v, "reason"); break;
            case "block" when UtilityDraft is CalendarBlockRowViewModel row:
                row.TitleEditor = V(v, "title"); row.StartText = V(v, "start"); row.EndText = V(v, "end"); break;
            case "event-edit" when UtilityDraft is CalendarEventRowViewModel row: ApplyEventEdit(v, row); break;
            case "journal" when UtilityDraft is JournalEditorViewModel row:
                row.Name = V(v, "name"); row.Description = V(v, "description"); break;
            case "journal-entry" when UtilityDraft is JournalEntryEditorViewModel row:
                row.JournalId = GuidV(v, "journal") ?? Guid.Empty; row.Title = V(v, "title");
                row.Content = V(v, "content");
                if (draft.PendingRequest is not null && DateV(v, "capturedOccurred") is { } occurred)
                    row.RestoreDate(V(v, "date"), occurred);
                else row.DateText = V(v, "date");
                row.TagsText = V(v, "tags");
                row.MoodRating = IntV(v, "mood"); break;
            case "habit" when UtilityDraft is HabitEditorViewModel row:
                row.Name = V(v, "name"); row.Description = V(v, "description"); row.StartDateText = V(v, "start"); break;
            case "bulk" when UtilityDraft is BulkTaskEditorViewModel row: ApplyBulk(v, row); break;
        }
    }

    private void RestorePendingUtilityRequest(UtilityEditorRecoveryDraft draft)
    {
        if (draft.PendingRequest is not { } request) return;
        var v = draft.Values;
        switch (draft.Kind)
        {
            case "manual":
            {
                var intent = new { TaskId = ManualTaskId, ActivityId = ManualActivityId,
                    Notes = string.IsNullOrWhiteSpace(ManualNotes) ? null : ManualNotes.Trim(),
                    StartText = ManualStartText, EndText = ManualEndText };
                if (DateV(v, "capturedStart") is { } start && DateV(v, "capturedEnd") is { } end)
                {
                    _manualRecoveryArguments = new(start, end);
                    _manualOperation.RestoreCaptured(intent,
                        new { Intent = intent, Start = start, End = end }, request);
                }
                else _manualOperation.RestoreIntent(intent, request);
                break;
            }
            case "activity": _createActivityOperation.RestoreIntent(new { Name = NewActivityName.Trim(),
                Description = NewActivityDescription.Trim(), Lane = NewActivityLane,
                GroupId = NewActivityGroupId == Guid.Empty ? (Guid?)null : NewActivityGroupId }, request); break;
            case "plan":
            {
                var intent = new { CalendarId = SelectedCalendarId, TaskId = CalendarPlanTaskId,
                    StartText = CalendarPlanStartText, EndText = CalendarPlanEndText };
                if (DateV(v, "capturedStart") is { } start && DateV(v, "capturedEnd") is { } end
                    && !string.IsNullOrWhiteSpace(V(v, "capturedZone")))
                {
                    var timeZone = V(v, "capturedZone");
                    var activityId = GuidV(v, "capturedActivity");
                    var title = V(v, "capturedTitle");
                    _planRecoveryArguments = new(start, end, timeZone, activityId, title);
                    _planOperation.RestoreCaptured(intent, new { Intent = intent, Start = start,
                        End = end, TimeZone = timeZone, ActivityId = activityId, Title = title }, request);
                }
                else _planOperation.RestoreIntent(intent, request);
                break;
            }
            case "event":
            {
                var intent = new
                {
                    CalendarId = SelectedCalendarId, Title = NewEventTitle.Trim(),
                    StartText = NewEventStart, EndText = NewEventEnd,
                    Description = NewEventDescription.Trim(),
                    Location = string.IsNullOrWhiteSpace(NewEventLocation) ? null : NewEventLocation.Trim(),
                    Color = string.IsNullOrWhiteSpace(NewEventColor) ? "#246B63" : NewEventColor.Trim(),
                    AllDay = NewEventAllDay,
                    Recurrence = string.IsNullOrWhiteSpace(NewEventRecurrence) ? null : NewEventRecurrence.Trim(),
                    RecurrenceEndText = NewEventRecurrenceEnd
                };
                if (DateV(v, "capturedStart") is { } start && DateV(v, "capturedEnd") is { } end
                    && !string.IsNullOrWhiteSpace(V(v, "capturedZone")))
                {
                    var timeZone = V(v, "capturedZone");
                    var recurrenceEnd = DateV(v, "capturedRecurrenceEnd");
                    _eventRecoveryArguments = new(start, end, timeZone, recurrenceEnd);
                    _createEventOperation.RestoreCaptured(intent, new { Intent = intent,
                        Start = start, End = end, TimeZone = timeZone,
                        RecurrenceEnd = recurrenceEnd }, request);
                }
                else _createEventOperation.RestoreIntent(intent, request);
                break;
            }
            case "journal" when UtilityDraft is JournalEditorViewModel row:
                row.Operation.Restore(new JournalDefinition(row.Name, row.Description), draft.ExpectedRevision, request); break;
            case "journal-entry" when UtilityDraft is JournalEntryEditorViewModel row:
                try { row.Operation.Restore(row.Definition(), draft.ExpectedRevision, request); }
                catch (SnookException) { }
                break;
            case "habit" when UtilityDraft is HabitEditorViewModel row:
                try
                {
                    object operation = row.Habit is { }
                        ? new HabitUpdate(row.Name, row.Description)
                        : new HabitDefinition(row.Name, row.Description,
                            DateOnly.ParseExact(row.StartDateText, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                            row.CreationTimeZone);
                    row.RestoreRequest(operation, draft.ExpectedRevision, request);
                }
                catch (FormatException) { }
                break;
            case "bulk" when UtilityDraft is BulkTaskEditorViewModel row:
                row.RestoreRequest(request); break;
        }
    }

    private object EventCreationIntent() => new
    {
        CalendarId = SelectedCalendarId, Title = NewEventTitle.Trim(), StartText = NewEventStart,
        EndText = NewEventEnd, Description = NewEventDescription.Trim(),
        Location = string.IsNullOrWhiteSpace(NewEventLocation) ? null : NewEventLocation.Trim(),
        Color = string.IsNullOrWhiteSpace(NewEventColor) ? "#246B63" : NewEventColor.Trim(),
        AllDay = NewEventAllDay, Recurrence = string.IsNullOrWhiteSpace(NewEventRecurrence)
            ? null : NewEventRecurrence.Trim(), RecurrenceEndText = NewEventRecurrenceEnd
    };

    private void CaptureEventCreation(Dictionary<string, string?> v)
    {
        v["calendar"] = S(SelectedCalendarId); v["title"] = NewEventTitle;
        v["start"] = NewEventStart; v["end"] = NewEventEnd; v["description"] = NewEventDescription;
        v["location"] = NewEventLocation; v["color"] = NewEventColor; v["allDay"] = B(NewEventAllDay);
        v["recurrence"] = NewEventRecurrence; v["recurrenceEnd"] = NewEventRecurrenceEnd;
    }

    private void ApplyEventCreation(IReadOnlyDictionary<string, string?> v)
    {
        SelectedCalendarId = GuidV(v, "calendar") ?? Guid.Empty; NewEventTitle = V(v, "title");
        NewEventStart = V(v, "start"); NewEventEnd = V(v, "end"); NewEventDescription = V(v, "description");
        NewEventLocation = V(v, "location"); NewEventColor = V(v, "color"); NewEventAllDay = BoolV(v, "allDay");
        NewEventRecurrence = V(v, "recurrence"); NewEventRecurrenceEnd = V(v, "recurrenceEnd");
    }

    private static void CaptureEventEdit(Dictionary<string, string?> v, CalendarEventRowViewModel row)
    {
        v["title"] = row.TitleEditor; v["start"] = row.StartText; v["end"] = row.EndText;
        v["description"] = row.DescriptionEditor; v["location"] = row.LocationEditor;
        v["color"] = row.ColorEditor; v["allDay"] = B(row.AllDayEditor);
        v["recurrence"] = row.RecurrenceEditor; v["recurrenceEnd"] = row.RecurrenceEndText;
    }

    private static void ApplyEventEdit(IReadOnlyDictionary<string, string?> v, CalendarEventRowViewModel row)
    {
        row.TitleEditor = V(v, "title"); row.StartText = V(v, "start"); row.EndText = V(v, "end");
        row.DescriptionEditor = V(v, "description"); row.LocationEditor = V(v, "location");
        row.ColorEditor = V(v, "color"); row.AllDayEditor = BoolV(v, "allDay");
        row.RecurrenceEditor = V(v, "recurrence"); row.RecurrenceEndText = V(v, "recurrenceEnd");
    }

    private static void CaptureBulk(Dictionary<string, string?> v, BulkTaskEditorViewModel row)
    {
        v["changeTitle"] = B(row.ChangeTitle); v["title"] = row.Title;
        v["changeDescription"] = B(row.ChangeDescription); v["description"] = row.Description;
        v["changePriority"] = B(row.ChangePriority); v["priority"] = I((int)row.Priority);
        v["changeStatus"] = B(row.ChangeStatus); v["completed"] = B(row.Completed);
        v["changeDue"] = B(row.ChangeDueDate); v["due"] = row.DueDateText;
        v["changeActivity"] = B(row.ChangeActivity); v["activity"] = S(row.ActivityId);
        v["changeStarred"] = B(row.ChangeStarred); v["starred"] = B(row.Starred);
        v["changeProject"] = B(row.ChangeProject); v["project"] = S(row.ProjectId);
        v["changeArchive"] = B(row.ChangeArchive); v["archived"] = B(row.Archived);
        v["changeTags"] = B(row.ChangeTags); v["addTags"] = row.AddTagsText; v["removeTags"] = row.RemoveTagsText;
    }

    private static void ApplyBulk(IReadOnlyDictionary<string, string?> v, BulkTaskEditorViewModel row)
    {
        row.ChangeTitle = BoolV(v, "changeTitle"); row.Title = V(v, "title");
        row.ChangeDescription = BoolV(v, "changeDescription"); row.Description = V(v, "description");
        row.ChangePriority = BoolV(v, "changePriority"); row.Priority = EnumV<Priority>(v, "priority");
        row.ChangeStatus = BoolV(v, "changeStatus"); row.Completed = BoolV(v, "completed");
        row.ChangeDueDate = BoolV(v, "changeDue"); row.DueDateText = V(v, "due");
        row.ChangeActivity = BoolV(v, "changeActivity"); row.ActivityId = GuidV(v, "activity") ?? Guid.Empty;
        row.ChangeStarred = BoolV(v, "changeStarred"); row.Starred = BoolV(v, "starred");
        row.ChangeProject = BoolV(v, "changeProject"); row.ProjectId = GuidV(v, "project") ?? Guid.Empty;
        row.ChangeArchive = BoolV(v, "changeArchive"); row.Archived = BoolV(v, "archived");
        row.ChangeTags = BoolV(v, "changeTags"); row.AddTagsText = V(v, "addTags");
        row.RemoveTagsText = V(v, "removeTags");
    }

    private static string V(IReadOnlyDictionary<string, string?> values, string key)
        => values.TryGetValue(key, out var value) ? value ?? string.Empty : string.Empty;
    private static string? S(Guid? value) => value?.ToString("D");
    private static string B(bool value) => value ? "1" : "0";
    private static string I(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static string O(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
    private static Guid? GuidV(IReadOnlyDictionary<string, string?> values, string key)
        => Guid.TryParse(V(values, key), out var value) && value != Guid.Empty ? value : null;
    private static bool BoolV(IReadOnlyDictionary<string, string?> values, string key) => V(values, key) == "1";
    private static int IntV(IReadOnlyDictionary<string, string?> values, string key)
        => int.TryParse(V(values, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
    private static T EnumV<T>(IReadOnlyDictionary<string, string?> values, string key) where T : struct, Enum
    {
        var raw = IntV(values, key);
        return Enum.IsDefined(typeof(T), raw) ? (T)Enum.ToObject(typeof(T), raw) : default;
    }
    private static DateTimeOffset Date(IReadOnlyDictionary<string, string?> values, string key)
        => DateTimeOffset.TryParseExact(V(values, key), "O", CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var value) ? value : throw new InvalidDataException("Invalid recovery anchor.");

    private static DateTimeOffset? DateV(IReadOnlyDictionary<string, string?> values, string key)
        => DateTimeOffset.TryParseExact(V(values, key), "O", CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var value) ? value : null;

    private sealed record ManualRecoveryArguments(DateTimeOffset Start, DateTimeOffset End);
    private sealed record PlanRecoveryArguments(DateTimeOffset Start, DateTimeOffset End,
        string TimeZone, Guid? ActivityId, string Title);
    private sealed record EventRecoveryArguments(DateTimeOffset Start, DateTimeOffset End,
        string TimeZone, DateTimeOffset? RecurrenceEnd);
}
