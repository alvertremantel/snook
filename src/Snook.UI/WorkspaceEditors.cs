using System.Windows.Input;
using Snook.Domain;

namespace Snook.UI;

public sealed partial class MainWindowViewModel
{
    private string _settingsPage = "General";
    private string? _utilityKind;
    private object? _utilityDraft;
    private string _utilityTitle = string.Empty;
    private ICommand? _utilitySaveCommand;

    public string SettingsPage
    {
        get => _settingsPage;
        private set
        {
            if (!SetField(ref _settingsPage, value)) return;
            foreach (var property in new[] { nameof(IsGeneralSettings), nameof(IsOrganizationSettings), nameof(IsActivitySettings), nameof(IsCalendarSettings) })
                RaisePropertyChanged(property);
        }
    }
    public bool IsGeneralSettings => SettingsPage == "General";
    public bool IsOrganizationSettings => SettingsPage == "Organization";
    public bool IsActivitySettings => SettingsPage == "Activities";
    public bool IsCalendarSettings => SettingsPage == "Calendars";
    public bool IsUtilityEditorOpen => _utilityKind is not null;
    public bool IsManualEditor => _utilityKind == "manual";
    public bool IsNewActivityEditor => _utilityKind == "activity";
    public bool IsPlanEditor => _utilityKind == "plan";
    public bool IsNewEventEditor => _utilityKind == "event";
    public object? UtilityDraft { get => _utilityDraft; private set => SetField(ref _utilityDraft, value); }
    public string UtilityTitle { get => _utilityTitle; private set => SetField(ref _utilityTitle, value); }
    public ICommand? UtilitySaveCommand { get => _utilitySaveCommand; private set => SetField(ref _utilitySaveCommand, value); }
    public string UtilitySaveLabel => _utilityKind switch
    {
        "habit" => UtilityDraft is HabitEditorViewModel { IsNew: true } ? "Create habit" : "Save habit",
        "bulk" => UtilityDraft is BulkTaskEditorViewModel batch ? $"Apply to {batch.Targets.Count} tasks" : "Apply changes",
        "manual" => "Add time",
        "activity" => "Create activity",
        "plan" => "Plan task",
        "event" => "Create event",
        _ => UtilityDraft is HistoryRowViewModel ? "Save correction" : "Save changes"
    };
    public ICommand OpenUtilityEditorCommand { get; private set; } = null!;
    public ICommand CloseUtilityEditorCommand { get; private set; } = null!;
    public ICommand SelectSettingsPageCommand { get; private set; } = null!;
    public ICommand OpenActivityLibraryCommand { get; private set; } = null!;
    public IReadOnlyList<ActivityOption> UtilityActivities { get; private set; } = [];
    public IReadOnlyList<ActivityGroupOption> UtilityGroups { get; private set; } = [];
    public IReadOnlyList<TaskOption> UtilityManualTasks { get; private set; } = [];
    public IReadOnlyList<TaskOption> UtilityPlanTasks { get; private set; } = [];
    public IReadOnlyList<CalendarOption> UtilityCalendars { get; private set; } = [];

    private void InitializeWorkspaceEditors()
    {
        OpenUtilityEditorCommand = new AsyncCommand(value => { OpenUtilityEditor(value); return Task.CompletedTask; });
        CloseUtilityEditorCommand = new AsyncCommand(_ => { CloseUtilityEditor(); return Task.CompletedTask; });
        SelectSettingsPageCommand = new AsyncCommand(value =>
        {
            SettingsPage = value as string is "Organization" or "Activities" or "Calendars" ? (string)value : "General";
            return Task.CompletedTask;
        });
        OpenActivityLibraryCommand = new AsyncCommand(async _ =>
        {
            SettingsPage = "Activities";
            await SelectSectionAsync("Settings");
        });
    }

    private void OpenUtilityEditor(object? source)
    {
        // Clone from persisted aggregates, never from mutable list-row drafts. Refresh can
        // replace the catalog while this independent draft keeps its original revision.
        object? draft = source switch
        {
            BoardAdminRowViewModel row => new BoardAdminRowViewModel(row.Board, SaveBoardAsync, ToggleBoardArchiveAsync, DeleteBoardAsync, ReorderBoardAsync),
            ProjectAdminRowViewModel row => new ProjectAdminRowViewModel(row.Project, SaveProjectAsync, ToggleProjectArchiveAsync, DeleteProjectAsync, AddProjectTagAsync, ReorderProjectAsync),
            ActivityAdminRowViewModel row => new ActivityAdminRowViewModel(row.Activity, ActivityGroups.ToArray(), SaveActivityAsync, ToggleActivityArchiveAsync, DeleteActivityAsync, AddActivityTagAsync, StartActivityAsync),
            ActivityGroupAdminRowViewModel row => new ActivityGroupAdminRowViewModel(row.Group, SaveActivityGroupAsync, DeleteActivityGroupAsync, ReorderActivityGroupAsync),
            CalendarAdminRowViewModel row => new CalendarAdminRowViewModel(row.Calendar, SaveCalendarAsync, DeleteCalendarAsync),
            HistoryRowViewModel row => new HistoryRowViewModel(row.Item, row.TaskOptions.ToArray(), row.ActivityOptions.ToArray(), CorrectHistoryAsync),
            CalendarBlockRowViewModel row => new CalendarBlockRowViewModel(row.Block, row.Label, UpdateCalendarBlockAsync, StartCalendarBlockAsync),
            CalendarEventRowViewModel row => new CalendarEventRowViewModel(row.Event, UpdateCalendarEventAsync, DeleteCalendarEventAsync),
            _ => null
        };
        var (title, command) = draft switch
        {
            BoardAdminRowViewModel row => ("Edit board", row.SaveCommand),
            ProjectAdminRowViewModel row => ("Edit project", row.SaveCommand),
            ActivityAdminRowViewModel row => ("Edit activity", row.SaveCommand),
            ActivityGroupAdminRowViewModel row => ("Edit activity group", row.SaveCommand),
            CalendarAdminRowViewModel row => ("Edit calendar", row.SaveCommand),
            HistoryRowViewModel row => ("Correct time entry", row.CorrectCommand),
            CalendarBlockRowViewModel row => ("Edit planned block", row.UpdateCommand),
            CalendarEventRowViewModel row => ("Edit event", row.UpdateCommand),
            _ => (source as string) switch
            {
                "manual" => ("Add manual time", ManualTimeCommand),
                "activity" => ("New activity", CreateActivityCommand),
                "plan" => ("Plan a task", PlanNextTaskCommand),
                "event" => ("New event", CreateCalendarEventCommand),
                _ => (string.Empty, (ICommand?)null)
            }
        };
        if (command is null) return;
        var calendarChoice = SelectedCalendarId;
        UtilityActivities = Activities.ToArray();
        UtilityGroups = ActivityGroups.ToArray();
        UtilityManualTasks = ManualTaskOptions.ToArray();
        UtilityPlanTasks = CalendarPlanTasks.ToArray();
        UtilityCalendars = CalendarOptions.ToArray();
        foreach (var property in new[] { nameof(UtilityActivities), nameof(UtilityGroups), nameof(UtilityManualTasks), nameof(UtilityPlanTasks), nameof(UtilityCalendars) })
            RaisePropertyChanged(property);
        ResetUtilityCreationDraft();
        // Snapshot replacement can clear a ComboBox's selection. Reapply the ID
        // after both calendar pickers have received their new item instances.
        SelectedCalendarId = Guid.Empty;
        SelectedCalendarId = UtilityCalendars.Any(calendar => calendar.Id == calendarChoice)
            ? calendarChoice : UtilityCalendars.Count > 0 ? UtilityCalendars[0].Id : Guid.Empty;
        UtilityDraft = draft;
        UtilityTitle = title;
        UtilitySaveCommand = command;
        _utilityKind = source is string kind ? kind : "edit";
        StatusMessage = draft is ProjectAdminRowViewModel or ActivityAdminRowViewModel
            ? "Save your edits, or Cancel to discard them. Adding a tag applies immediately."
            : "Changes stay in this editor until you save.";
        NotifyUtilityEditor();
    }

    private void CloseUtilityEditor()
    {
        if (IsUtilityEditorOpen) StatusMessage = "Ready.";
        _utilityKind = null;
        UtilityDraft = null;
        UtilitySaveCommand = null;
        ResetUtilityCreationDraft();
        NotifyUtilityEditor();
    }

    private void FinishUtilityEdit(object source)
    {
        if (ReferenceEquals(UtilityDraft, source) || source is string kind && kind == _utilityKind)
        {
            var message = StatusMessage;
            CloseUtilityEditor();
            StatusMessage = message;
        }
    }

    private void ResetUtilityCreationDraft()
    {
        ManualTaskId = null;
        ManualActivityId = null;
        ManualStartText = FormatLocalDateTime(DateTimeOffset.Now.AddMinutes(-30));
        ManualEndText = FormatLocalDateTime(DateTimeOffset.Now);
        ManualNotes = string.Empty;
        NewActivityName = string.Empty;
        NewActivityDescription = string.Empty;
        NewActivityLane = SessionLane.Foreground;
        NewActivityGroupId = Guid.Empty;
        NewEventTitle = NewEventDescription = NewEventLocation = NewEventRecurrence = NewEventRecurrenceEnd = string.Empty;
        NewEventColor = "#246B63";
        NewEventAllDay = false;
        NewEventStart = FormatLocalDateTime(DateTimeOffset.Now.AddHours(1));
        NewEventEnd = FormatLocalDateTime(DateTimeOffset.Now.AddHours(2));
        CalendarPlanTaskId = null;
        CalendarPlanStartText = NewEventStart;
        CalendarPlanEndText = NewEventEnd;
        RaisePropertyChanged(nameof(CalendarPlanTaskId));
        RaisePropertyChanged(nameof(CalendarPlanStartText));
        RaisePropertyChanged(nameof(CalendarPlanEndText));
    }

    private void NotifyUtilityEditor()
    {
        foreach (var property in new[] { nameof(IsUtilityEditorOpen), nameof(IsManualEditor), nameof(IsNewActivityEditor), nameof(IsPlanEditor), nameof(IsNewEventEditor) })
            RaisePropertyChanged(property);
        RaisePropertyChanged(nameof(UtilitySaveLabel));
    }

    private static string EditorFailure(Exception exception, string fallback) => exception switch
    {
        SnookException { Code: SnookErrorCode.RevisionConflict } conflict =>
            $"{conflict.Message} Your draft is still here. Copy any changes you need, then cancel and reopen to load the latest version.",
        SnookException failure => failure.Message,
        _ => fallback
    };
}
