using System.Collections.ObjectModel;
using System.Windows.Input;
using Snook.Contracts;
using Snook.Domain;

namespace Snook.UI;

public sealed partial class MainWindowViewModel
{
    public ObservableCollection<TrackerActivityGroup> TrackerGroups { get; } = [];
    public ObservableCollection<ActiveSessionRowViewModel> FocusSessions { get; } = [];
    public ObservableCollection<ActiveSessionRowViewModel> BackgroundSessions { get; } = [];
    public bool HasNoFocusSessions => FocusSessions.Count == 0;
    public bool HasNoBackgroundSessions => BackgroundSessions.Count == 0;
    public bool HasNoTrackerActivities => TrackerGroups.Count == 0;
    public ActiveSessionRowViewModel? PrimarySession => FocusSessions.FirstOrDefault(s => s.Session.State == SessionState.Running) ?? FocusSessions.FirstOrDefault();
    public IEnumerable<ActiveSessionRowViewModel> TodayFocusSessions => PrimarySession is { } session ? [session] : [];
    public IEnumerable<TaskRowViewModel> TodayNextTasks => Tasks.Where(t => t.Task.Status != TaskState.Completed && t.Task.ArchivedAtUtc is null && t.Task.DeletedAtUtc is null).Take(4);
    public bool HasNoTodayNextTasks => !TodayNextTasks.Any();
    public IEnumerable<ReviewRowViewModel> TodayAgendaPreview => TodayUpcomingRows.Take(3);
    public string TodayOverview => $"{TrackedToday} tracked today · {OpenTaskCount} open tasks";
    public string SessionCountLabel => $"{ActiveSessions.Count} in progress";

    private void RefreshTracker(BootstrapSnapshot snapshot)
    {
        FocusSessions.Clear();
        BackgroundSessions.Clear();
        foreach (var session in ActiveSessions)
            (session.Session.Lane == SessionLane.Background ? BackgroundSessions : FocusSessions).Add(session);

        TrackerGroups.Clear();
        var groupNames = (snapshot.ActivityGroups ?? []).Where(g => g.DeletedAtUtc is null)
            .ToDictionary(g => g.Id, g => g.Name);
        var available = snapshot.Activities.Where(a => a.ArchivedAtUtc is null && a.DeletedAtUtc is null);
        foreach (var group in available.GroupBy(a => a.GroupId).OrderBy(g => g.Key is null))
        {
            var rows = group.Select(activity =>
            {
                var session = ActiveSessions.FirstOrDefault(s => s.Session.TaskId is null && s.Session.ActivityId == activity.Id);
                var state = session?.Session.State;
                return new TrackerActivity(activity.Name, activity.Description,
                    activity.DefaultLane == SessionLane.Background ? "Background" : "Focus",
                    state == SessionState.Running ? "Running" : state == SessionState.Paused ? "Resume" : "Start",
                    state == SessionState.Running,
                    new AsyncCommand(_ => StartOrSwitchActivityAsync(activity.Id, activity.DefaultLane)));
            }).ToArray();
            TrackerGroups.Add(new TrackerActivityGroup(
                group.Key is { } id && groupNames.TryGetValue(id, out var name) ? name : "Everyday", rows));
        }
        RaisePropertyChanged(nameof(HasNoFocusSessions));
        RaisePropertyChanged(nameof(HasNoBackgroundSessions));
        RaisePropertyChanged(nameof(SessionCountLabel));
        RaisePropertyChanged(nameof(PrimarySession));
        RaisePropertyChanged(nameof(TodayFocusSessions));
        RaisePropertyChanged(nameof(TodayOverview));
        RaisePropertyChanged(nameof(HasNoTrackerActivities));
    }
}

public sealed record TrackerActivityGroup(string Name, IReadOnlyList<TrackerActivity> Activities)
{
    public string CountLabel => $"{Activities.Count} activities";
}

public sealed record TrackerActivity(string Name, string Description, string LaneLabel,
    string ActionLabel, bool IsRunning, ICommand StartCommand);
