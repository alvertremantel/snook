using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Snook.Application;

namespace Snook.Android;

// Development host: process-scoped workspace ownership, activity-scoped views.
public sealed class SpikeApp : UI.App
{
    internal SpikeProbe Probe { get; } = new();
    private readonly AndroidWorkspace _workspace = new();
    private readonly HashSet<MainActivity> _visibleActivities = [];

    internal void ReceiveEntry(AndroidEntryRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        switch (route.Kind)
        {
            case AndroidEntryKind.NormalLaunch:
                return;
            case AndroidEntryKind.SharedText when route.Value is not null:
                _workspace.ReceiveSharedText(route.Value);
                return;
            case AndroidEntryKind.TimerOpen when route.Value is not null:
                _ = Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    var message = await _workspace.OpenTimerAsync(route.Value);
                    if (!string.IsNullOrEmpty(message)) MainActivity.Current?.ShowTransientMessage(message);
                });
                return;
            case AndroidEntryKind.QuickCapture:
                _ = Dispatcher.UIThread.InvokeAsync(() => _workspace.OpenSectionAsync("Tasks"));
                return;
            case AndroidEntryKind.TimeTracker:
                _ = Dispatcher.UIThread.InvokeAsync(() => _workspace.OpenSectionAsync("Time Tracker"));
                return;
            case AndroidEntryKind.JournalEntry:
                _ = Dispatcher.UIThread.InvokeAsync(() => _workspace.OpenJournalEntryAsync());
                return;
            case AndroidEntryKind.Today:
                _ = Dispatcher.UIThread.InvokeAsync(() => _workspace.OpenSectionAsync("Today"));
                return;
            case AndroidEntryKind.Habits:
                _ = Dispatcher.UIThread.InvokeAsync(() => _workspace.OpenSectionAsync("Habits"));
                return;
            case AndroidEntryKind.RejectedShare:
                _workspace.RejectEntry(share: true);
                return;
            default:
                _workspace.RejectEntry(share: false);
                return;
        }
    }

    internal Task<string> HandleTimerActionAsync(string token, CancellationToken cancellationToken)
        => _workspace.HandleTimerActionAsync(token, cancellationToken);

    internal Task ReconcileTimerNotificationsAsync(CancellationToken cancellationToken)
        => _workspace.ReconcileTimerNotificationsAsync(cancellationToken);

    internal Task ReconcileRemindersAsync(CancellationToken cancellationToken)
        => _workspace.ReconcileRemindersAsync(cancellationToken);

    internal Task FireReminderAsync(Guid taskId, Guid workspaceId,
        CancellationToken cancellationToken)
        => _workspace.FireReminderAsync(taskId, workspaceId, cancellationToken);

    internal void SetActivityVisible(MainActivity activity, bool visible)
    {
        if (visible) _visibleActivities.Add(activity);
        else _visibleActivities.Remove(activity);
        _workspace.SetForeground(_visibleActivities.Count > 0);
    }

    internal void SetAppLocked(bool locked) => _ = Dispatcher.UIThread.InvokeAsync(
        () => _workspace.SetAppLockedAsync(locked));

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IActivityApplicationLifetime activity)
            activity.MainViewFactory = () => new AndroidHostView(_workspace, Probe);
        base.OnFrameworkInitializationCompleted();
    }
}
