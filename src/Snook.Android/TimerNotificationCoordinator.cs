using System.Buffers.Binary;
using System.Security.Cryptography;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Snook.Application;
using Snook.Contracts;
using Snook.Domain;

namespace Snook.Android;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "Owned for the Android application process lifetime.")]
internal sealed class TimerNotificationCoordinator
{
    internal const string OpenAction = AndroidEntryRouter.TimerOpenAction;
    internal const string TokenExtra = "action_token";
    private const string ChannelId = "active-timers-v1";
    private const string GroupPrefix = "org.snook.spike.timers.";
    private const string PreferencesName = "snook-timer-notifications";
    private const int SummarySalt = 0x534E4F4B;
    private readonly TimerActionStore _actions;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _clock;
    private IBackendClient? _backend;
    private Guid _workspaceId;

    internal TimerNotificationCoordinator(string noBackupRoot, TimeProvider? clock = null)
    {
        _actions = new TimerActionStore(noBackupRoot);
        _clock = clock ?? TimeProvider.System;
    }

    internal async Task AttachAsync(IBackendClient backend, Guid workspaceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(backend);
        if (workspaceId == Guid.Empty) throw new ArgumentException("A workspace is required.", nameof(workspaceId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(_backend, backend))
            {
                if (_backend is not null) _backend.Changed -= BackendChanged;
                _backend = backend;
                _backend.Changed += BackendChanged;
            }
            _workspaceId = workspaceId;
            await ReconcileCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    internal async Task DetachAsync(bool clearNative, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_backend is not null) _backend.Changed -= BackendChanged;
            _backend = null;
            _workspaceId = Guid.Empty;
            if (clearNative)
            {
                CancelPosted();
                await _actions.ClearAsync(cancellationToken).ConfigureAwait(false);
                TimerWidgetCoordinator.RenderUnavailable("Choose a workspace in Snook");
            }
        }
        finally { _gate.Release(); }
    }

    internal async Task<string> EnableAsync(CancellationToken cancellationToken = default)
    {
        var activity = MainActivity.Current;
        if (activity is null) return "Return to Snook before enabling timer notifications.";
        if (!await activity.RequestNotificationPermissionAsync().ConfigureAwait(true))
            return "Timer notifications remain off. Timers still work in Snook; enable notifications later in Android settings.";

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureChannel();
            if (!CanPost())
                return "The Active timers channel is disabled in Android settings. Timers still work inside Snook.";
            await ReconcileCoreAsync(cancellationToken).ConfigureAwait(false);
            return "Timer notifications enabled. Running and paused timers will stay synchronized with committed workspace state.";
        }
        catch (Exception exception)
        {
            global::Android.Util.Log.Warn("SnookTimer", $"Enable: {exception.GetType().Name}");
            return "Timer notifications could not be initialized. Timers still work inside Snook.";
        }
        finally { _gate.Release(); }
    }

    internal static Task<string> ReviewPromotionAccessAsync()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(36))
            return Task.FromResult("Live Updates require Android 16. Ordinary timer notifications remain available.");
        var activity = MainActivity.Current;
        if (activity is null)
            return Task.FromResult("Return to Snook before reviewing Live Update access.");
        using var manager = (NotificationManager)activity.GetSystemService(Context.NotificationService)!;
        if (manager.CanPostPromotedNotifications())
            return Task.FromResult("Android Live Update access is enabled for eligible running timers.");
        using var intent = new Intent(global::Android.Provider.Settings.ActionAppNotificationPromotionSettings);
        intent.PutExtra(global::Android.Provider.Settings.ExtraAppPackage, activity.PackageName);
        var packageManager = activity.PackageManager;
        if (packageManager is null || intent.ResolveActivity(packageManager) is null)
            return Task.FromResult("This Android build does not provide Live Update access settings. Ordinary timer notifications remain available.");
        activity.StartActivity(intent);
        return Task.FromResult("Review Live Update access in Android settings, then return to Snook. Ordinary timer notifications remain available.");
    }

    internal Task ReconcileNowAsync(CancellationToken cancellationToken = default)
        => ReconcileAsync(cancellationToken);

    internal async Task<string> ExecuteAsync(string token, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var action = await _actions.ResolveAsync(token, _clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            if (_backend is not { } backend || action.WorkspaceId != _workspaceId)
                return "That timer action belongs to another workspace. Open Snook to review the current timers.";
            var snapshot = await backend.GetBootstrapAsync(cancellationToken).ConfigureAwait(false);
            if (snapshot.Workspace.Id != action.WorkspaceId)
                return "That timer action belongs to another workspace. Open Snook to review the current timers.";
            var item = snapshot.Today.ActiveSessions.FirstOrDefault(candidate => candidate.Session.Id == action.SessionId);
            if (item is null || item.Session.Revision != action.ExpectedRevision)
            {
                await ReconcileCoreAsync(cancellationToken).ConfigureAwait(false);
                return "That timer changed. Open Snook to review its current state.";
            }
            var request = new OperationRequest(action.OperationId, action.ClientDeviceId, action.ExpectedRevision);
            switch (action.Kind)
            {
                case TimerActionKind.Pause when item.Session.State == SessionState.Running:
                    await backend.PauseSessionAsync(item.Session.Id, request, cancellationToken).ConfigureAwait(false);
                    break;
                case TimerActionKind.Resume when item.Session.State == SessionState.Paused:
                    await backend.ResumeSessionAsync(item.Session.Id, request, cancellationToken).ConfigureAwait(false);
                    break;
                case TimerActionKind.Stop when item.Session.State is SessionState.Running or SessionState.Paused:
                    await backend.StopSessionAsync(item.Session.Id, null, request, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    await ReconcileCoreAsync(cancellationToken).ConfigureAwait(false);
                    return "That timer action is no longer valid. Open Snook to review its current state.";
            }
            await ReconcileCoreAsync(cancellationToken).ConfigureAwait(false);
            return action.Kind switch
            {
                TimerActionKind.Pause => "Timer paused.",
                TimerActionKind.Resume => "Timer resumed.",
                _ => "Timer stopped."
            };
        }
        catch (SnookException exception) when (exception.Code is SnookErrorCode.RevisionConflict
            or SnookErrorCode.NotFound or SnookErrorCode.InvalidTransition)
        {
            await TryReconcileCoreAsync().ConfigureAwait(false);
            return "That timer changed. Open Snook to review its current state.";
        }
        catch (Exception exception)
        {
            global::Android.Util.Log.Warn("SnookTimer", $"Action: {exception.GetType().Name}");
            return "The timer action could not be confirmed. Open Snook and reconcile before retrying.";
        }
        finally { _gate.Release(); }
    }

    internal async Task<bool> ValidateOpenAsync(string token, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var action = await _actions.ResolveAsync(token, _clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            if (action.Kind != TimerActionKind.Open || action.WorkspaceId != _workspaceId || _backend is not { } backend)
                return false;
            var snapshot = await backend.GetBootstrapAsync(cancellationToken).ConfigureAwait(false);
            return snapshot.Workspace.Id == action.WorkspaceId
                && snapshot.Today.ActiveSessions.Any(item => item.Session.Id == action.SessionId);
        }
        catch (Exception exception)
        {
            global::Android.Util.Log.Warn("SnookTimer", $"Open: {exception.GetType().Name}");
            return false;
        }
        finally { _gate.Release(); }
    }

    private void BackendChanged(object? sender, ChangeNotification change) => _ = ReconcileAsync(CancellationToken.None);

    private async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await ReconcileCoreAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) { global::Android.Util.Log.Warn("SnookTimer", $"Reconcile: {exception.GetType().Name}"); }
        finally { _gate.Release(); }
    }

    private async Task TryReconcileCoreAsync()
    {
        try { await ReconcileCoreAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception exception) { global::Android.Util.Log.Warn("SnookTimer", $"Refresh: {exception.GetType().Name}"); }
    }

    private async Task ReconcileCoreAsync(CancellationToken cancellationToken)
    {
        if (_backend is not { } backend || _workspaceId == Guid.Empty) return;
        var bootstrap = await backend.GetBootstrapAsync(cancellationToken).ConfigureAwait(false);
        if (bootstrap.Workspace.Id != _workspaceId)
            throw new InvalidOperationException("The timer projection changed workspace identity.");
        var projection = TimerNotificationProjection.Build(bootstrap);
        TimerWidgetCoordinator.Render(projection);
        await WorkspaceWidgetCoordinator.RenderAsync(backend, bootstrap,
            cancellationToken).ConfigureAwait(false);
        EnsureChannel();
        if (!CanPost())
        {
            CancelPosted();
            await _actions.ClearAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var tokens = new HashSet<string>(StringComparer.Ordinal);
        var posted = new HashSet<int>();
        var context = global::Android.App.Application.Context;
        using var manager = (NotificationManager)context.GetSystemService(Context.NotificationService)!;
        foreach (var item in projection.Items)
        {
            var open = await TokenAsync(item, TimerActionKind.Open, projection.CapturedAtUtc, tokens, cancellationToken).ConfigureAwait(false);
            var primaryKind = item.State == SessionState.Running ? TimerActionKind.Pause : TimerActionKind.Resume;
            var primary = await TokenAsync(item, primaryKind, projection.CapturedAtUtc, tokens, cancellationToken).ConfigureAwait(false);
            var stop = await TokenAsync(item, TimerActionKind.Stop, projection.CapturedAtUtc, tokens, cancellationToken).ConfigureAwait(false);
            var id = NotificationId(projection.WorkspaceId, item.SessionId);
            posted.Add(id);
            using var notification = BuildNotification(context, projection, item, open, primary, stop);
            manager.Notify(id, notification);
        }
        if (projection.TotalActiveCount > 1)
        {
            var id = SummaryId(projection.WorkspaceId);
            posted.Add(id);
            using var summary = BuildSummary(context, projection);
            manager.Notify(id, summary);
        }
        CancelPostedExcept(manager, posted);
        SavePosted(posted);
        await _actions.RetainAsync(tokens, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> TokenAsync(TimerNotificationItem item, TimerActionKind kind, DateTimeOffset now,
        HashSet<string> retained, CancellationToken cancellationToken)
    {
        var token = await _actions.GetOrCreateAsync(_workspaceId, item.SessionId, item.Revision, kind,
            now, cancellationToken).ConfigureAwait(false);
        retained.Add(token);
        return token;
    }

    private static Notification BuildNotification(Context context, TimerNotificationSnapshot projection,
        TimerNotificationItem item, string openToken, string primaryToken, string stopToken)
    {
        using var content = ActivityIntent(context, openToken);
        using var primary = BroadcastIntent(context, primaryToken);
        using var stop = BroadcastIntent(context, stopToken);
        using var publicBuilder = new Notification.Builder(context, ChannelId);
        publicBuilder.SetSmallIcon(global::Android.Resource.Drawable.IcLockIdleAlarm)
            .SetContentTitle("Snook timer")
            .SetContentText(item.State == SessionState.Running ? "Running" : "Paused")
            .SetVisibility(NotificationVisibility.Public);
        using var publicVersion = publicBuilder.Build();
        using var builder = new Notification.Builder(context, ChannelId);
        var showDetails = NotificationPrivacy.ShowDetails;
        builder.SetSmallIcon(global::Android.Resource.Drawable.IcLockIdleAlarm)
            .SetContentTitle(showDetails ? item.Title : "Snook timer")
            .SetContentText(showDetails
                ? item.State == SessionState.Running ? item.Context : $"{item.Context} · Paused · {Duration(item.DisplayedMilliseconds)}"
                : item.State == SessionState.Running ? "Running" : $"Paused · {Duration(item.DisplayedMilliseconds)}")
            .SetSubText(item.Lane == SessionLane.Background ? "Background timer" : "Focus timer")
            .SetOngoing(true)
            .SetOnlyAlertOnce(true)
            .SetVisibility(NotificationVisibility.Private)
            .SetPublicVersion(publicVersion)
            .SetGroup(GroupPrefix + projection.WorkspaceId.ToString("N"))
            .SetContentIntent(content);
        builder.SetCategory(OperatingSystem.IsAndroidVersionAtLeast(31)
            ? Notification.CategoryStopwatch : Notification.CategoryProgress);
        if (item.State == SessionState.Running)
        {
            builder.SetWhen(projection.CapturedAtUtc.ToUnixTimeMilliseconds() - item.DisplayedMilliseconds)
                .SetShowWhen(true)
                .SetUsesChronometer(true);
            if (OperatingSystem.IsAndroidVersionAtLeast(36)
                && projection.Items.First(candidate => candidate.State == SessionState.Running).SessionId == item.SessionId)
            {
                using var extras = new Bundle();
                extras.PutBoolean("android.requestPromotedOngoing", true);
                builder.AddExtras(extras);
            }
        }
        else builder.SetShowWhen(false);
        using var primaryIcon = global::Android.Graphics.Drawables.Icon.CreateWithResource(context,
            item.State == SessionState.Running ? global::Android.Resource.Drawable.IcMediaPause : global::Android.Resource.Drawable.IcMediaPlay);
        using var stopIcon = global::Android.Graphics.Drawables.Icon.CreateWithResource(context,
            global::Android.Resource.Drawable.IcDelete);
        using var primaryTitle = new Java.Lang.String(item.State == SessionState.Running ? "Pause" : "Resume");
        using var stopTitle = new Java.Lang.String("Stop");
        using var primaryAction = new Notification.Action.Builder(primaryIcon, primaryTitle, primary).Build();
        using var stopAction = new Notification.Action.Builder(stopIcon, stopTitle, stop).Build();
        builder.AddAction(primaryAction);
        builder.AddAction(stopAction);
        return builder.Build();
    }

    private static Notification BuildSummary(Context context, TimerNotificationSnapshot projection)
    {
        var running = projection.Items.Count(item => item.State == SessionState.Running);
        var shown = projection.Items.Count;
        var detail = projection.TotalActiveCount == shown
            ? $"{running} running · {shown - running} paused"
            : $"{shown} shown · {projection.TotalActiveCount} total";
        using var builder = new Notification.Builder(context, ChannelId);
        builder.SetSmallIcon(global::Android.Resource.Drawable.IcLockIdleAlarm)
            .SetContentTitle($"{projection.TotalActiveCount} Snook timers")
            .SetContentText(detail)
            .SetOnlyAlertOnce(true)
            .SetVisibility(NotificationVisibility.Private)
            .SetCategory(Notification.CategoryStatus)
            .SetGroup(GroupPrefix + projection.WorkspaceId.ToString("N"))
            .SetGroupSummary(true);
        return builder.Build();
    }

    private static PendingIntent ActivityIntent(Context context, string token)
    {
        using var intent = new Intent(context, typeof(MainActivity));
        intent.SetAction(OpenAction);
        intent.SetData(global::Android.Net.Uri.Parse("snook-internal://timer/" + token));
        intent.PutExtra(TokenExtra, token);
        intent.SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
        return PendingIntent.GetActivity(context, RequestCode(token), intent,
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)
            ?? throw new InvalidOperationException("The timer destination could not be created.");
    }

    private static PendingIntent BroadcastIntent(Context context, string token)
    {
        using var intent = new Intent(context, typeof(TimerActionReceiver));
        intent.SetAction(TimerActionReceiver.Action);
        intent.SetData(global::Android.Net.Uri.Parse("snook-internal://timer-action/" + token));
        intent.PutExtra(TokenExtra, token);
        return PendingIntent.GetBroadcast(context, RequestCode(token), intent,
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)
            ?? throw new InvalidOperationException("The timer action could not be created.");
    }

    private static void EnsureChannel()
    {
        var context = global::Android.App.Application.Context;
        using var manager = (NotificationManager)context.GetSystemService(Context.NotificationService)!;
        using var channel = new NotificationChannel(ChannelId, "Active timers", NotificationImportance.Low)
        {
            Description = "Running and paused Snook timers",
            LockscreenVisibility = NotificationVisibility.Private
        };
        manager.CreateNotificationChannel(channel);
    }

    private static bool CanPost()
    {
        var context = global::Android.App.Application.Context;
        if (OperatingSystem.IsAndroidVersionAtLeast(33)
            && context.CheckSelfPermission(global::Android.Manifest.Permission.PostNotifications) != Permission.Granted)
            return false;
        using var manager = (NotificationManager)context.GetSystemService(Context.NotificationService)!;
        using var channel = manager.GetNotificationChannel(ChannelId);
        return manager.AreNotificationsEnabled() && channel?.Importance != NotificationImportance.None;
    }

    private static void CancelPosted()
    {
        var context = global::Android.App.Application.Context;
        using var manager = (NotificationManager)context.GetSystemService(Context.NotificationService)!;
        CancelPostedExcept(manager, []);
        SavePosted([]);
    }

    private static void CancelPostedExcept(NotificationManager manager, HashSet<int> retained)
    {
        foreach (var id in LoadPosted())
            if (!retained.Contains(id)) manager.Cancel(id);
    }

    private static HashSet<int> LoadPosted()
    {
        using var preferences = global::Android.App.Application.Context.GetSharedPreferences(
            PreferencesName, FileCreationMode.Private);
        var encoded = preferences?.GetString("ids", null);
        return encoded?.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(value => int.TryParse(value, out var id) ? id : 0)
            .Where(id => id > 0).Take(33).ToHashSet() ?? [];
    }

    private static void SavePosted(IEnumerable<int> ids)
    {
        using var preferences = global::Android.App.Application.Context.GetSharedPreferences(
            PreferencesName, FileCreationMode.Private)!;
        using var editor = preferences.Edit()!;
        editor.PutString("ids", string.Join(',', ids.Order()));
        if (!editor.Commit()) throw new IOException("Timer notification state could not be saved.");
    }

    private static int NotificationId(Guid workspaceId, Guid sessionId)
    {
        Span<byte> identity = stackalloc byte[32];
        workspaceId.TryWriteBytes(identity[..16]);
        sessionId.TryWriteBytes(identity[16..]);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(identity, hash);
        return (BinaryPrimitives.ReadInt32LittleEndian(hash) & 0x3FFFFFFF) + 10_000;
    }

    private static int SummaryId(Guid workspaceId)
    {
        Span<byte> identity = stackalloc byte[20];
        workspaceId.TryWriteBytes(identity[..16]);
        BinaryPrimitives.WriteInt32LittleEndian(identity[16..], SummarySalt);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(identity, hash);
        return (BinaryPrimitives.ReadInt32LittleEndian(hash) & 0x3FFFFFFF) + 10_000;
    }

    private static int RequestCode(string token)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Convert.FromHexString(token), hash);
        return BinaryPrimitives.ReadInt32LittleEndian(hash) & int.MaxValue;
    }

    private static string Duration(long milliseconds)
    {
        var duration = TimeSpan.FromMilliseconds(milliseconds);
        return duration.TotalHours >= 1 ? $"{(int)duration.TotalHours}h {duration.Minutes}m" : $"{duration.Minutes}m";
    }
}
