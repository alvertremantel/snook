using Android.App;
using Android.Content;
using Android.OS;
using Android.Widget;
using Avalonia.Threading;

namespace Snook.Android;

[BroadcastReceiver(Name = "org.snook.spike.TimerActionReceiver", Enabled = true, Exported = false)]
public sealed class TimerActionReceiver : BroadcastReceiver
{
    internal const string Action = "org.snook.spike.TIMER_ACTION";

    public override void OnReceive(Context? context, Intent? intent)
    {
        var token = intent?.GetStringExtra(TimerNotificationCoordinator.TokenExtra);
        var data = intent?.Data;
        if (context is null || intent?.Action != Action || token is null
            || data?.Scheme != "snook-internal" || data.Host != "timer-action"
            || !string.Equals(data.LastPathSegment, token, StringComparison.Ordinal))
            return;
        var pending = GoAsync();
        if (pending is null) return;
        _ = CompleteAsync(context.ApplicationContext!, token, pending);
    }

    private static async Task CompleteAsync(Context context, string token, PendingResult pending)
    {
        var message = "The timer action could not be confirmed. Open Snook to reconcile.";
        try
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            if (Avalonia.Application.Current is SpikeApp app)
                message = await Dispatcher.UIThread.InvokeAsync(() => app.HandleTimerActionAsync(token, budget.Token));
        }
        catch (Exception exception)
        {
            global::Android.Util.Log.Warn("SnookTimer", $"Receiver: {exception.GetType().Name}");
        }
        finally
        {
            using var handler = new Handler(Looper.MainLooper!);
            handler.Post(() => Toast.MakeText(context, message, ToastLength.Short)?.Show());
            pending.Finish();
        }
    }
}
