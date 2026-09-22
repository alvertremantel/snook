using Android.App;
using Android.Content;
using System.Security.Cryptography;

namespace Snook.Android;

[BroadcastReceiver(Name = "org.snook.spike.ProbeReceiver", Enabled = true, Exported = false)]
public sealed class ProbeReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (intent?.Action != ReceiverSpike.Action) return;
        ReceiverSpike.Accept(intent.GetStringExtra("nonce"));
    }
}

internal static class ReceiverSpike
{
    internal const string Action = "org.snook.spike.CHECK_RECEIVER";
    private static readonly object Gate = new();
    private static string? _nonce;
    private static TaskCompletionSource? _received;

    internal static bool Accept(string? nonce)
    {
        lock (Gate)
        {
            if (_nonce is null || !string.Equals(_nonce, nonce, StringComparison.Ordinal)) return false;
            _nonce = null;
            _received?.TrySetResult();
            return true;
        }
    }

    public static async Task RunAsync()
    {
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (Gate)
        {
            if (_received is not null) throw new InvalidOperationException("A receiver probe is already active.");
            _nonce = nonce;
            _received = received;
        }
        try
        {
            if (Accept("invalid")) throw new InvalidOperationException("An invalid action nonce was accepted.");
            var context = global::Android.App.Application.Context;
            using var intent = new Intent(context, typeof(ProbeReceiver));
            intent.SetAction(Action);
            intent.PutExtra("nonce", nonce);
            using var pending = PendingIntent.GetBroadcast(context, 7901, intent,
                PendingIntentFlags.Immutable | PendingIntentFlags.OneShot | PendingIntentFlags.CancelCurrent)
                ?? throw new InvalidOperationException("The native action could not be created.");
            // A holder must not be able to replace the opaque action nonce.
            using var substitution = new Intent();
            substitution.PutExtra("nonce", "substitution-attempt");
            pending.Send(context, 0, substitution);
            await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (Accept(nonce)) throw new InvalidOperationException("An action nonce was replayed.");
            var rejected = false;
            try { pending.Send(); }
            catch (PendingIntent.CanceledException) { rejected = true; }
            if (!rejected) throw new InvalidOperationException("A one-shot native action was replayed.");
        }
        finally
        {
            lock (Gate)
            {
                _nonce = null;
                _received = null;
            }
        }
    }
}
