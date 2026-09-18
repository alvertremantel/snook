using System.Windows.Input;
using Snook.Domain;

namespace Snook.UI;

public sealed partial class MainWindowViewModel
{
    private DateTime _calendarAnchor = DateTime.Today;
    private string _calendarRangeLabel = string.Empty;
    private CalendarGridItemViewModel? _selectedCalendarItem;
    private string _calendarMode = "Event";
    private string _eventCalendarView = "Week";
    private string _historyCalendarView = "Flex";
    private int _calendarFlexDays = 5;
    private int _calendarLoadVersion;
    private IReadOnlyList<HistoryItem> _calendarHistory = [];
    private DateTime _historyGridStart;
    private DateTime _historyGridEnd;
    private DateTimeOffset _nextCalendarTick;
    private string _calendarHistoryNotice = string.Empty;
    public string CalendarMode => _calendarMode;
    public bool IsCalendarEventMode => CalendarMode == "Event";
    public bool IsCalendarHistoryMode => CalendarMode == "History";
    public bool IsCalendarFlex => CalendarView == "Flex";
    public string CalendarHistoryNotice
    {
        get => _calendarHistoryNotice;
        private set { if (SetField(ref _calendarHistoryNotice, value)) RaisePropertyChanged(nameof(HasCalendarHistoryNotice)); }
    }
    public bool HasCalendarHistoryNotice => CalendarHistoryNotice.Length > 0;
    public string CalendarHistoryLegend => CalendarDayColumns.Any(day => day.Items.Any(item => item.IsActual))
        ? "Recorded time · Future plans in gray" : "No recorded time in this range · Future plans in gray";
    public bool CanEditCalendarItemInAgenda => IsCalendarEventMode && HasSelectedCalendarItem;
    public ICommand SelectCalendarModeCommand { get; private set; } = null!;
    public DateTime CalendarAnchor => _calendarAnchor;
    public string CalendarRangeLabel { get => _calendarRangeLabel; private set => SetField(ref _calendarRangeLabel, value); }
    public string CalendarTimeZoneLabel { get; } = $"Local time · {TimeZoneInfo.Local.Id}";
    public Guid? CalendarPlanTaskId { get; set; }
    public string CalendarPlanStartText { get; set; } = FormatLocalDateTime(DateTimeOffset.Now.AddHours(1));
    public string CalendarPlanEndText { get; set; } = FormatLocalDateTime(DateTimeOffset.Now.AddHours(2));
    public System.Collections.ObjectModel.ObservableCollection<TaskOption> CalendarPlanTasks { get; } = [];
    public bool IsCalendarTimelineVisible => IsCalendarDay || IsCalendarWeek || IsCalendarFlex;
    public CalendarGridItemViewModel? SelectedCalendarItem
    {
        get => _selectedCalendarItem;
        private set
        {
            if (!SetField(ref _selectedCalendarItem, value)) return;
            RaisePropertyChanged(nameof(HasSelectedCalendarItem));
            RaisePropertyChanged(nameof(CanEditCalendarItemInAgenda));
        }
    }
    public bool HasSelectedCalendarItem => SelectedCalendarItem is not null;
    public ICommand NavigateCalendarCommand { get; private set; } = null!;
    public ICommand CloseCalendarItemCommand { get; private set; } = null!;

    private void InitializeCalendarCommands()
    {
        SelectCalendarModeCommand = new AsyncCommand(mode => SelectCalendarModeAsync(mode as string ?? "Event"));
        NavigateCalendarCommand = new AsyncCommand(async direction =>
        {
            try
            {
                var step = direction as string == "previous" ? -1 : 1;
                _calendarAnchor = direction as string == "today" ? DateTime.Today
                    : IsCalendarMonth ? _calendarAnchor.AddMonths(step)
                    : _calendarAnchor.AddDays(step * (IsCalendarDay ? 1 : IsCalendarAgenda ? 14 : IsCalendarFlex ? _calendarFlexDays : 7));
                SelectedCalendarItem = null;
                RaisePropertyChanged(nameof(CalendarAnchor));
                await ReloadCalendarAsync();
            }
            catch (Exception)
            {
                StatusMessage = "That calendar range could not be loaded.";
            }
        });
        CloseCalendarItemCommand = new AsyncCommand(_ => { SelectedCalendarItem = null; return Task.CompletedTask; });
    }

    private void InspectCalendarItem(CalendarGridItemViewModel item) => SelectedCalendarItem = item;

    private async Task SelectCalendarModeAsync(string mode)
    {
        mode = mode == "History" ? "History" : "Event";
        if (_calendarMode == mode) return;
        _calendarMode = mode;
        SelectedCalendarItem = null;
        CalendarView = IsCalendarHistoryMode ? _historyCalendarView : _eventCalendarView;
        RaisePropertyChanged(nameof(CalendarMode));
        RaisePropertyChanged(nameof(IsCalendarEventMode));
        RaisePropertyChanged(nameof(IsCalendarHistoryMode));
        RaisePropertyChanged(nameof(CanEditCalendarItemInAgenda));
        await ReloadCalendarAsync();
    }

    // Called after layout settles, including when returning from another workspace.
    public async Task SetCalendarViewportWidthAsync(double width)
    {
        var count = CalendarHistoryProjection.FlexDayCount(width);
        if (count == _calendarFlexDays) return;
        _calendarFlexDays = count;
        if (IsCalendarVisible && IsCalendarFlex) await ReloadCalendarAsync();
    }

    private async Task ReloadCalendarAsync()
    {
        ++_calendarLoadVersion;
        _historyGridEnd = default;
        CalendarDayColumns.Clear();
        CalendarRangeLabel = "Loading calendar…";
        try { await LoadCalendarAsync(await _backend.GetBootstrapAsync()); }
        catch (Exception)
        {
            CalendarRangeLabel = "Calendar unavailable";
            StatusMessage = "That calendar range could not be loaded. Use Refresh to try again.";
        }
    }

    private void RefreshCalendarHistory(DateTimeOffset now)
    {
        if (!IsCalendarVisible || !IsCalendarHistoryMode || now < _nextCalendarTick || _historyGridEnd <= _historyGridStart) return;
        _nextCalendarTick = now.AddSeconds(15);
        var columns = CalendarHistoryProjection.Build(_historyGridStart, _historyGridEnd, _calendarHistory,
            CalendarBlocks.Select(row => CalendarGridItemViewModel.ForBlock(row, InspectCalendarItem)), now, TimeZoneInfo.Local, InspectCalendarItem);
        CalendarDayColumns.Clear();
        foreach (var column in columns) CalendarDayColumns.Add(column);
        RaisePropertyChanged(nameof(CalendarHistoryLegend));
        if (SelectedCalendarItem is { } selected)
            SelectedCalendarItem = columns.SelectMany(column => column.Items).FirstOrDefault(item => item.Key == selected.Key);
    }
}
