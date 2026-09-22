using Snook.Application;

namespace Snook.UI;

public sealed partial class MainWindowViewModel
{
    public WorkspaceNavigationState CaptureNavigationState(Guid workspaceId) => new(
        1,
        workspaceId,
        CurrentSection,
        TaskViewMode,
        TaskBoardId,
        TaskSortMode,
        CalendarMode,
        _eventCalendarView,
        _historyCalendarView,
        DateOnly.FromDateTime(_calendarAnchor),
        SummaryGrouping,
        SettingsPage,
        SelectedJournalId);

    public async Task<bool> RestoreNavigationStateAsync(WorkspaceNavigationState state,
        Guid workspaceId)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (_disposed || !state.IsValid(workspaceId) || IsTaskEditorOpen || IsUtilityEditorOpen)
            return false;

        TaskViewMode = state.TaskView;
        _taskSortMode = state.TaskSort;
        RaisePropertyChanged(nameof(TaskSortMode));
        TaskBoardId = TaskBoardOptions.Any(board => board.Id == state.TaskBoardId)
            ? state.TaskBoardId
            : Guid.Empty;

        _calendarMode = state.CalendarMode;
        _eventCalendarView = state.EventCalendarView;
        _historyCalendarView = state.HistoryCalendarView;
        CalendarView = _calendarMode == "History" ? _historyCalendarView : _eventCalendarView;
        _calendarAnchor = state.CalendarAnchor.ToDateTime(TimeOnly.MinValue);
        RaisePropertyChanged(nameof(CalendarMode));
        RaisePropertyChanged(nameof(IsCalendarEventMode));
        RaisePropertyChanged(nameof(IsCalendarHistoryMode));
        RaisePropertyChanged(nameof(CanEditCalendarItemInAgenda));
        RaisePropertyChanged(nameof(CalendarAnchor));

        SummaryGrouping = state.SummaryGrouping;
        SettingsPage = state.SettingsPage;
        _selectedJournalId = state.SelectedJournalId;
        RaisePropertyChanged(nameof(SelectedJournalId));

        await SelectSectionAsync(state.Section);
        return true;
    }
}
