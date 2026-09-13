using System.Windows.Input;

namespace Snook.UI;

public sealed partial class MainWindowViewModel
{
    private DateTime _calendarAnchor = DateTime.Today;
    private string _calendarRangeLabel = string.Empty;
    private CalendarGridItemViewModel? _selectedCalendarItem;
    public DateTime CalendarAnchor => _calendarAnchor;
    public string CalendarRangeLabel { get => _calendarRangeLabel; private set => SetField(ref _calendarRangeLabel, value); }
    public string CalendarTimeZoneLabel { get; } = $"Local time · {TimeZoneInfo.Local.Id}";
    public Guid? CalendarPlanTaskId { get; set; }
    public string CalendarPlanStartText { get; set; } = FormatLocalDateTime(DateTimeOffset.Now.AddHours(1));
    public string CalendarPlanEndText { get; set; } = FormatLocalDateTime(DateTimeOffset.Now.AddHours(2));
    public System.Collections.ObjectModel.ObservableCollection<TaskOption> CalendarPlanTasks { get; } = [];
    public bool IsCalendarTimelineVisible => IsCalendarDay || IsCalendarWeek;
    public CalendarGridItemViewModel? SelectedCalendarItem
    {
        get => _selectedCalendarItem;
        private set { if (SetField(ref _selectedCalendarItem, value)) RaisePropertyChanged(nameof(HasSelectedCalendarItem)); }
    }
    public bool HasSelectedCalendarItem => SelectedCalendarItem is not null;
    public ICommand NavigateCalendarCommand { get; private set; } = null!;
    public ICommand CloseCalendarItemCommand { get; private set; } = null!;

    private void InitializeCalendarCommands()
    {
        NavigateCalendarCommand = new AsyncCommand(async direction =>
        {
            try
            {
                var step = direction as string == "previous" ? -1 : 1;
                _calendarAnchor = direction as string == "today" ? DateTime.Today
                    : IsCalendarMonth ? _calendarAnchor.AddMonths(step)
                    : _calendarAnchor.AddDays(step * (IsCalendarDay ? 1 : IsCalendarAgenda ? 14 : 7));
                SelectedCalendarItem = null;
                RaisePropertyChanged(nameof(CalendarAnchor));
                await LoadCalendarAsync(await _backend.GetBootstrapAsync());
            }
            catch (Exception)
            {
                StatusMessage = "That calendar range could not be loaded.";
            }
        });
        CloseCalendarItemCommand = new AsyncCommand(_ => { SelectedCalendarItem = null; return Task.CompletedTask; });
    }

    private void InspectCalendarItem(CalendarGridItemViewModel item) => SelectedCalendarItem = item;
}
