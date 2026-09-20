using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Android.App;
using Android.Content;
using AndroidX.Work;
using Java.Util.Concurrent;
using Snook.Application;
using Snook.Contracts;

namespace Snook.Android;

internal sealed class ReminderIndexStore(string root)
{
    private readonly string _path = Path.Combine(root, "reminders", "index.bin");
    private readonly KeystoreProtector _protector = new("org.snook.reminders.v1", 64 * 1024);

    internal void Save(ReminderNotificationSnapshot snapshot)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(snapshot,
            ReminderIndexJson.Default.ReminderNotificationSnapshot);
        byte[] encrypted;
        try { encrypted = _protector.Protect(plain, "index"); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var staging = _path + ".tmp";
        try
        {
            using (var stream = new FileStream(staging, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(encrypted);
                stream.Flush(true);
            }
            File.Move(staging, _path, true);
        }
        finally { File.Delete(staging); }
    }

    internal ReminderNotificationSnapshot? Load(DateTimeOffset now)
    {
        if (!File.Exists(_path)) return null;
        var encrypted = File.ReadAllBytes(_path);
        if (encrypted.Length is < 1 or > 65_565) throw new InvalidDataException("Invalid reminder index size.");
        var plain = _protector.Unprotect(encrypted, "index");
        try
        {
            var value = JsonSerializer.Deserialize(plain,
                ReminderIndexJson.Default.ReminderNotificationSnapshot);
            return value is { Version: ReminderNotificationSnapshot.CurrentVersion }
                && value.WorkspaceId != Guid.Empty && value.CapturedAtUtc.Offset == TimeSpan.Zero
                && value.FreshUntilUtc >= now && value.Items.Count <= ReminderNotificationSnapshot.MaximumItems
                ? value : null;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    internal void Clear() => File.Delete(_path);
}

[JsonSerializable(typeof(ReminderNotificationSnapshot))]
internal sealed partial class ReminderIndexJson : JsonSerializerContext;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "Owned for an Android process/session lifetime; short-lived instances finish all gated work before release.")]
internal sealed class ReminderCoordinator
{
    private const string ChannelId = "task-reminders-v1";
    private const string AlarmAction = "org.snook.spike.action.REMINDER";
    internal const string TaskExtra = "task_id";
    internal const string WorkspaceExtra = "workspace_id";
    private readonly ReminderIndexStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _clock;
    private IBackendClient? _backend;
    private Guid _workspaceId;

    internal ReminderCoordinator(string root, TimeProvider? clock = null)
    {
        _store = new ReminderIndexStore(root);
        _clock = clock ?? TimeProvider.System;
    }

    internal async Task AttachAsync(IBackendClient backend, Guid workspaceId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(_backend, backend))
            {
                if (_backend is not null) _backend.Changed -= Changed;
                _backend = backend;
                backend.Changed += Changed;
            }
            _workspaceId = workspaceId;
            await ReconcileCoreAsync(cancellationToken).ConfigureAwait(false);
            ReminderWorkScheduler.EnsureScheduled();
        }
        finally { _gate.Release(); }
    }

    internal async Task DetachAsync(bool clearNative,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_backend is not null) _backend.Changed -= Changed;
            _backend = null;
            _workspaceId = Guid.Empty;
            if (clearNative) { CancelAll(); _store.Clear(); }
        }
        finally { _gate.Release(); }
    }

    internal async Task<string> EnableAndTestAsync(CancellationToken cancellationToken = default)
    {
        if (MainActivity.Current is not { } activity)
            return "Return to Snook before enabling reminder notifications.";
        if (!await activity.RequestNotificationPermissionAsync().ConfigureAwait(true))
            return "Reminder notifications remain off. Timed deadlines still work in Snook; enable notifications later in Android settings.";
        EnsureChannel();
        await ReconcileNowAsync(cancellationToken).ConfigureAwait(false);
        PostTest();
        return "Reminder notifications are enabled. A test notification was posted; Android may delay real reminders under Doze or battery restrictions.";
    }

    internal async Task ReconcileNowAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await ReconcileCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private void Changed(object? sender, ChangeNotification change) => _ = ReconcileSafeAsync();

    private async Task ReconcileSafeAsync()
    {
        try { await ReconcileNowAsync().ConfigureAwait(false); }
        catch (Exception exception) { global::Android.Util.Log.Warn("SnookReminder", $"Reconcile: {exception.GetType().Name}"); }
    }

    private async Task ReconcileCoreAsync(CancellationToken cancellationToken)
    {
        if (_backend is not { } backend || _workspaceId == Guid.Empty) return;
        var bootstrap = await backend.GetBootstrapAsync(cancellationToken).ConfigureAwait(false);
        if (bootstrap.Workspace.Id != _workspaceId)
            throw new InvalidOperationException("Reminder projection changed workspace identity.");
        var tasks = await backend.SearchTasksAsync(includeCompleted: true,
            includeArchived: true, cancellationToken: cancellationToken).ConfigureAwait(false);
        var snapshot = ReminderNotificationSnapshot.Build(_workspaceId, tasks,
            _clock.GetUtcNow());
        _store.Save(snapshot);
        Schedule(snapshot);
    }

    internal static void Schedule(ReminderNotificationSnapshot snapshot)
    {
        CancelAll();
        var now = DateTimeOffset.UtcNow;
        foreach (var item in snapshot.Items.Where(item => item.DueAtUtc > now))
        {
            using var intent = new Intent(global::Android.App.Application.Context,
                typeof(ReminderAlarmReceiver));
            intent.SetAction(AlarmAction);
            intent.PutExtra(TaskExtra, item.TaskId.ToString("N"));
            intent.PutExtra(WorkspaceExtra, snapshot.WorkspaceId.ToString("N"));
            using var pending = PendingIntent.GetBroadcast(global::Android.App.Application.Context,
                AlarmId(item.ReminderId), intent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
            using var alarms = (AlarmManager)global::Android.App.Application.Context
                .GetSystemService(Context.AlarmService)!;
            alarms.SetAndAllowWhileIdle(AlarmType.RtcWakeup,
                item.DueAtUtc.ToUnixTimeMilliseconds(), pending!);
        }
        var preferences = global::Android.App.Application.Context.GetSharedPreferences(
            "snook-reminder-alarms", FileCreationMode.Private)!;
        using var editor = preferences.Edit();
        editor!.PutString("ids", string.Join(',', snapshot.Items.Select(item => item.ReminderId.ToString("N"))));
        editor.Apply();
    }

    internal static void CancelAll()
    {
        var context = global::Android.App.Application.Context;
        var preferences = context.GetSharedPreferences("snook-reminder-alarms", FileCreationMode.Private)!;
        var ids = preferences.GetString("ids", "")!.Split(',', StringSplitOptions.RemoveEmptyEntries);
        using var alarms = (AlarmManager)context.GetSystemService(Context.AlarmService)!;
        foreach (var value in ids)
        {
            if (!Guid.TryParseExact(value, "N", out var id)) continue;
            using var intent = new Intent(context, typeof(ReminderAlarmReceiver));
            intent.SetAction(AlarmAction);
            using var pending = PendingIntent.GetBroadcast(context, AlarmId(id), intent,
                PendingIntentFlags.NoCreate | PendingIntentFlags.Immutable);
            if (pending is not null) alarms.Cancel(pending);
        }
        preferences.Edit()!.Remove("ids")!.Apply();
    }

    internal static void Post(ReminderDescriptor item, bool staleAttached)
    {
        EnsureChannel();
        var context = global::Android.App.Application.Context;
        using var open = new Intent(context, typeof(MainActivity));
        open.SetAction(AndroidEntryRouter.TodayAction);
        using var pending = PendingIntent.GetActivity(context, AlarmId(item.ReminderId), open,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
        using var builder = new Notification.Builder(context, ChannelId);
        var showDetails = NotificationPrivacy.ShowDetails;
        builder.SetSmallIcon(global::Android.Resource.Drawable.IcLockIdleAlarm)
            .SetContentTitle(staleAttached ? "Snook reminder (last synced)"
                : showDetails ? item.Title : "Snook task reminder")
            .SetContentText(staleAttached ? "Open Snook to reconnect and verify this timed deadline." : "Timed task deadline")
            .SetContentIntent(pending).SetAutoCancel(true)
            .SetVisibility(NotificationVisibility.Private)
            .SetCategory(Notification.CategoryReminder);
        using var notification = builder.Build();
        using var manager = (NotificationManager)context.GetSystemService(Context.NotificationService)!;
        manager.Notify(AlarmId(item.ReminderId), notification);
    }

    private static void PostTest()
    {
        EnsureChannel();
        var context = global::Android.App.Application.Context;
        using var builder = new Notification.Builder(context, ChannelId);
        builder.SetSmallIcon(global::Android.Resource.Drawable.IcLockIdleAlarm)
            .SetContentTitle("Snook test reminder")
            .SetContentText("Reminder notifications can appear on this device.")
            .SetAutoCancel(true).SetVisibility(NotificationVisibility.Public);
        using var notification = builder.Build();
        using var manager = (NotificationManager)context.GetSystemService(Context.NotificationService)!;
        manager.Notify(0x534E5254, notification);
    }

    private static void EnsureChannel()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return;
        var context = global::Android.App.Application.Context;
        using var manager = (NotificationManager)context.GetSystemService(Context.NotificationService)!;
        using var channel = new NotificationChannel(ChannelId, "Task reminders",
            NotificationImportance.Default)
        { Description = "Inexact reminders for explicitly timed task deadlines" };
        manager.CreateNotificationChannel(channel);
    }

    private static int AlarmId(Guid id)
    {
        var bytes = id.ToByteArray();
        return BitConverter.ToInt32(bytes, 0) & int.MaxValue;
    }
}

[BroadcastReceiver(Name = "org.snook.spike.ReminderAlarmReceiver", Enabled = true,
    Exported = false, DirectBootAware = false)]
[IntentFilter(["org.snook.spike.action.REMINDER"])]
public sealed class ReminderAlarmReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null || intent?.Action != "org.snook.spike.action.REMINDER"
            || !Guid.TryParseExact(intent.GetStringExtra(ReminderCoordinator.TaskExtra), "N", out var taskId)
            || !Guid.TryParseExact(intent.GetStringExtra(ReminderCoordinator.WorkspaceExtra), "N", out var workspaceId)) return;
        var pending = GoAsync();
        if (pending is not null) _ = ReminderBackground.FireAsync(pending, taskId, workspaceId);
    }
}

internal static class ReminderWorkScheduler
{
    internal static void EnsureScheduled()
    {
        using var request = PeriodicWorkRequest.Builder.From<ReminderReconcileWorker>(
            6, TimeUnit.Hours!).Build();
        WorkManager.GetInstance(global::Android.App.Application.Context)
            .EnqueueUniquePeriodicWork("snook-reminder-reconcile",
                ExistingPeriodicWorkPolicy.Update!, request);
    }
}

public sealed class ReminderReconcileWorker : Worker
{
    public ReminderReconcileWorker(Context context, WorkerParameters parameters)
        : base(context, parameters) { }

    public override Result DoWork()
    {
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            ReminderBackground.ReconcileSelectedAsync(cancellation.Token).GetAwaiter().GetResult();
            return Result.InvokeSuccess()!;
        }
        catch (Exception exception)
        {
            global::Android.Util.Log.Warn("SnookReminder", $"Worker: {exception.GetType().Name}");
            return Result.InvokeRetry()!;
        }
    }
}
