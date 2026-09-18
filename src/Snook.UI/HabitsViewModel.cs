using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Windows.Input;
using Snook.Contracts;
using Snook.Domain;

namespace Snook.UI;

public sealed partial class MainWindowViewModel
{
    private bool _showArchivedHabits;
    private bool _showDeletedHabits;
    private string _habitThroughText = string.Empty;
    private DateOnly? _habitThrough;
    private int _habitLoadVersion;
    private DateTimeOffset _nextHabitDayCheck;
    public bool IsHabitsVisible => CurrentSection == "Habits";
    public ObservableCollection<HabitRowViewModel> Habits { get; } = [];
    public bool HasNoHabits => Habits.Count == 0;
    public bool ShowArchivedHabits { get => _showArchivedHabits; set { if (SetField(ref _showArchivedHabits, value)) _ = LoadHabitsAsync(); } }
    public bool ShowDeletedHabits { get => _showDeletedHabits; set { if (SetField(ref _showDeletedHabits, value)) _ = LoadHabitsAsync(); } }
    public string HabitThroughText { get => _habitThroughText; set => SetField(ref _habitThroughText, value); }
    public ICommand NewHabitCommand { get; private set; } = null!;
    public ICommand ApplyHabitDateCommand { get; private set; } = null!;
    public ICommand HabitTodayCommand { get; private set; } = null!;

    private void InitializeHabitCommands()
    {
        NewHabitCommand = new AsyncCommand(_ => { OpenHabitEditor(null); return Task.CompletedTask; }, disableWhileRunning: false);
        ApplyHabitDateCommand = new AsyncCommand(async _ =>
        {
            if (!DateOnly.TryParseExact(HabitThroughText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                || date.Year < 1900 || date > DateOnly.FromDateTime(DateTime.Now).AddDays(1))
            { StatusMessage = "Enter a past or current date as YYYY-MM-DD."; return; }
            _habitThrough = date;
            await LoadHabitsAsync();
        });
        HabitTodayCommand = new AsyncCommand(async _ => { _habitThrough = null; HabitThroughText = string.Empty; await LoadHabitsAsync(); });
    }

    private async Task LoadHabitsAsync()
    {
        var version = ++_habitLoadVersion;
        try
        {
            var progress = await _backend.GetHabitsAsync(new HabitQuery(30, _habitThrough, ShowArchivedHabits, ShowDeletedHabits));
            if (version != _habitLoadVersion) return;
            var ids = progress.Select(item => item.Habit.Id).ToHashSet();
            foreach (var removed in Habits.Where(row => !ids.Contains(row.Habit.Id)).ToArray()) Habits.Remove(removed);
            foreach (var item in progress)
            {
                var row = Habits.FirstOrDefault(row => row.Habit.Id == item.Habit.Id);
                if (row is null) Habits.Add(new HabitRowViewModel(item, ChangeHabitDayAsync, ChangeHabitLifecycleAsync, OpenHabitEditor));
                else row.Refresh(item);
            }
            RaisePropertyChanged(nameof(HasNoHabits));
        }
        catch (Exception exception) { StatusMessage = exception.Message; }
    }

    private void OpenHabitEditor(HabitRowViewModel? row)
    {
        var draft = new HabitEditorViewModel(row?.Habit, SaveHabitAsync, ReviewHabitAsync);
        UtilityDraft = draft;
        UtilityTitle = row is null ? "New daily habit" : "Edit daily habit";
        UtilitySaveCommand = draft.SaveCommand;
        _utilityKind = "habit";
        StatusMessage = "Changes stay in this editor until you save.";
        NotifyUtilityEditor();
    }

    private async Task SaveHabitAsync(HabitEditorViewModel draft)
    {
        try
        {
            if (draft.Habit is { } habit)
            {
                var update = new HabitUpdate(draft.Name, draft.Description);
                await _backend.UpdateHabitAsync(habit.Id, update, draft.OperationFor(update, habit.Revision));
            }
            else
            {
                if (!DateOnly.TryParseExact(draft.StartDateText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start))
                    throw new SnookException(SnookErrorCode.ValidationFailed, "Enter the start date as YYYY-MM-DD.");
                var definition = new HabitDefinition(draft.Name, draft.Description, start, TimeZoneInfo.Local.Id);
                await _backend.CreateHabitAsync(definition, draft.OperationFor(definition, null));
            }
            StatusMessage = "Habit saved.";
            FinishUtilityEdit(draft);
            await LoadHabitsAsync();
        }
        catch (Exception exception) { StatusMessage = exception.Message; }
    }

    private async Task ReviewHabitAsync(HabitEditorViewModel draft)
    {
        try
        {
            var latest = (await _backend.GetHabitsAsync(new HabitQuery(1, IncludeArchived: true, IncludeDeleted: true)))
                .FirstOrDefault(item => item.Habit.Id == draft.Habit?.Id)?.Habit
                ?? throw new SnookException(SnookErrorCode.NotFound, "The saved habit could not be found.");
            if (latest.ArchivedAtUtc is not null || latest.DeletedAtUtc is not null)
                throw new SnookException(SnookErrorCode.InvalidTransition, "This habit is archived or deleted. Cancel, restore it in Habits, then edit again.");
            draft.Review(latest);
            StatusMessage = "Latest revision loaded. Compare the saved details below with your draft, then Save to apply your edits.";
        }
        catch (Exception exception) { StatusMessage = exception.Message; }
    }

    private async Task ChangeHabitDayAsync(HabitRowViewModel row, HabitDayViewModel day)
    {
        try
        {
            var completed = !day.Completed;
            await _backend.SetHabitCompletionAsync(row.Habit.Id, day.Date, completed, row.OperationFor(new { day.Date, completed }));
            await LoadHabitsAsync();
            StatusMessage = completed ? "Day checked off." : "Check-in undone.";
        }
        catch (Exception exception) { await LoadHabitsAsync(); StatusMessage = exception.Message; }
    }

    private async Task ChangeHabitLifecycleAsync(HabitRowViewModel row, bool delete)
    {
        try
        {
            if (delete)
                await _backend.SetHabitDeletedAsync(row.Habit.Id, !row.IsDeleted, row.OperationFor(new { Deleted = !row.IsDeleted }));
            else
                await _backend.SetHabitArchivedAsync(row.Habit.Id, !row.IsArchived, row.OperationFor(new { Archived = !row.IsArchived }));
            await LoadHabitsAsync();
            StatusMessage = "Habit updated. Its check-in history is preserved.";
        }
        catch (Exception exception) { await LoadHabitsAsync(); StatusMessage = exception.Message; }
    }

    private void RefreshHabitDay(DateTimeOffset now)
    {
        if (!IsHabitsVisible || now < _nextHabitDayCheck) return;
        _nextHabitDayCheck = now.AddSeconds(30);
        if (Habits.Any(row => row.Progress.Today != HabitRules.Today(now, row.Habit.TimeZone))) _ = LoadHabitsAsync();
    }
}

public sealed class HabitRowViewModel : INotifyPropertyChanged
{
    private readonly Func<HabitRowViewModel, HabitDayViewModel, Task> _changeDay;
    private readonly HabitOperation _operation = new();
    public HabitRowViewModel(HabitProgress progress, Func<HabitRowViewModel, HabitDayViewModel, Task> changeDay,
        Func<HabitRowViewModel, bool, Task> lifecycle, Action<HabitRowViewModel> edit)
    {
        Progress = progress;
        _changeDay = changeDay;
        EditCommand = new AsyncCommand(_ => { edit(this); return Task.CompletedTask; }, disableWhileRunning: false);
        ArchiveCommand = new AsyncCommand(_ => lifecycle(this, false));
        DeleteCommand = new AsyncCommand(_ => lifecycle(this, true));
        Refresh(progress);
        RecentDays = Days.TakeLast(7).ToArray();
        EarlierDays = Days.SkipLast(7).ToArray();
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public HabitProgress Progress { get; private set; }
    public Habit Habit => Progress.Habit;
    public string Name => Habit.Name;
    public string Description => Habit.Description;
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
    public bool IsDeleted => Habit.DeletedAtUtc is not null;
    public bool IsArchived => Habit.ArchivedAtUtc is not null;
    public bool IsActive => !IsDeleted && !IsArchived;
    public bool CanArchive => !IsDeleted;
    public string StateLabel => IsDeleted ? "Deleted · history retained" : IsArchived ? "Archived · history retained" : "Daily";
    public string ArchiveLabel => IsArchived ? "Restore archived" : "Archive";
    public string DeleteLabel => IsDeleted ? "Restore deleted" : "Delete";
    public string ProgressLabel => $"{Progress.CurrentStreak} day current streak  ·  {Progress.CompletedDays} / {Progress.EligibleDays} days done in this view";
    public string RangeLabel => $"{Progress.RangeStart:MMM d} – {Progress.RangeEnd:MMM d, yyyy}  ·  {Habit.TimeZone}";
    public string EditName => $"Edit habit {Name}";
    public string ArchiveName => $"{ArchiveLabel} habit {Name}";
    public string DeleteName => $"{DeleteLabel} habit {Name}";
    public ICommand EditCommand { get; }
    public ICommand ArchiveCommand { get; }
    public ICommand DeleteCommand { get; }
    public ObservableCollection<HabitDayViewModel> Days { get; } = [];
    public IReadOnlyList<HabitDayViewModel> RecentDays { get; }
    public IReadOnlyList<HabitDayViewModel> EarlierDays { get; }
    public OperationRequest OperationFor(object value) => _operation.For(value, Habit.Revision);
    public void Refresh(HabitProgress progress)
    {
        Progress = progress;
        for (var offset = 0; offset < 30; offset++)
        {
            var date = progress.RangeStart.AddDays(offset);
            if (Days.Count <= offset) Days.Add(new HabitDayViewModel(this, date, _changeDay));
            Days[offset].Refresh(date);
        }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
}

public sealed class HabitDayViewModel : INotifyPropertyChanged
{
    private readonly HabitRowViewModel _owner;
    public HabitDayViewModel(HabitRowViewModel owner, DateOnly date, Func<HabitRowViewModel, HabitDayViewModel, Task> change)
    {
        _owner = owner;
        Date = date;
        // Keep keyboard focus on this day while saving, but still suppress repeats.
        ToggleCommand = new AsyncCommand(_ => change(owner, this), _ => CanCheck, disableWhileRunning: false);
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public DateOnly Date { get; private set; }
    public bool Completed => _owner.Progress.CheckIns.Any(item => item.Date == Date);
    public bool CanCheck => _owner.IsActive && Date >= _owner.Habit.StartDate && Date <= _owner.Progress.Today
        && Date.DayNumber >= _owner.Progress.Today.DayNumber - HabitRules.MaximumDays + 1;
    public string DateLabel => Date == _owner.Progress.Today ? "Today" : Date.ToString("ddd d", CultureInfo.CurrentCulture);
    public string StateLabel => Completed ? "✓ Done" : Date < _owner.Habit.StartDate ? "—" : Date > _owner.Progress.Today ? "Future" : "Open";
    public string AutomationName => $"{(Completed ? "Undo" : "Check off")} {_owner.Name} on {Date:yyyy-MM-dd}";
    public string Detail => $"{Date:yyyy-MM-dd} · {StateLabel} · {_owner.Habit.TimeZone}";
    public ICommand ToggleCommand { get; }
    public void Refresh(DateOnly date)
    {
        Date = date;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        ((AsyncCommand)ToggleCommand).RaiseCanExecuteChanged();
    }
}

public sealed class HabitEditorViewModel : INotifyPropertyChanged
{
    private readonly HabitOperation _operation = new();
    public HabitEditorViewModel(Habit? habit, Func<HabitEditorViewModel, Task> save, Func<HabitEditorViewModel, Task> review)
    {
        Habit = habit;
        Name = habit?.Name ?? string.Empty;
        Description = habit?.Description ?? string.Empty;
        StartDateText = (habit?.StartDate ?? DateOnly.FromDateTime(DateTime.Now)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        SaveCommand = new AsyncCommand(_ => save(this));
        ReviewCommand = new AsyncCommand(_ => review(this));
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public Habit? Habit { get; private set; }
    public bool IsNew => Habit is null;
    public bool IsExisting => !IsNew;
    public string Name { get; set; }
    public string Description { get; set; }
    public string StartDateText { get; set; }
    public string ZoneLabel => $"Daily in {Habit?.TimeZone ?? TimeZoneInfo.Local.Id}. Start date and time zone stay fixed after creation.";
    public string SavedDetails { get; private set; } = string.Empty;
    public ICommand SaveCommand { get; }
    public ICommand ReviewCommand { get; }
    public OperationRequest OperationFor(object value, long? revision) => _operation.For(value, revision);
    public void Review(Habit habit)
    {
        Habit = habit;
        SavedDetails = $"Saved name: {habit.Name}\nSaved description: {habit.Description}\nYour draft above is unchanged.";
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SavedDetails)));
    }
}

internal sealed class HabitOperation
{
    private string? _fingerprint;
    private OperationRequest? _request;
    private readonly Guid _deviceId = Guid.NewGuid();
    public OperationRequest For(object value, long? revision)
    {
        var fingerprint = JsonSerializer.Serialize(new { value, revision });
        if (_request is null || fingerprint != _fingerprint)
        {
            _fingerprint = fingerprint;
            _request = new OperationRequest(Guid.NewGuid(), _deviceId, revision);
        }
        return _request;
    }
}
