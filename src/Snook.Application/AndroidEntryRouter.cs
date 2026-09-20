namespace Snook.Application;

public enum AndroidEntryKind
{
    NormalLaunch,
    SharedText,
    TimerOpen,
    QuickCapture,
    TimeTracker,
    JournalEntry,
    Today,
    Habits,
    RejectedShare,
    Rejected
}

/// <summary>
/// Primitive metadata extracted by the Android adapter. No parcel, URI object,
/// stream, clip or backend object crosses into the shared router.
/// </summary>
public sealed record AndroidEntryRequest(
    string? Action,
    string? MimeType,
    string? DataScheme,
    string? DataHost,
    string? DataPath,
    string? DataQuery,
    string? DataFragment,
    int DataPort,
    string? Text,
    int ExtraCount,
    bool HasTextExtra,
    bool HasTokenExtra,
    bool HasStream,
    bool HasClipData);

public sealed record AndroidEntryRoute(AndroidEntryKind Kind, string? Value = null);

/// <summary>
/// Fail-closed classification for every cold/warm activity entry currently exposed
/// by the Android composition root. Entity/workspace validation remains the owning
/// coordinator's second gate.
/// </summary>
public static class AndroidEntryRouter
{
    public const string MainAction = "android.intent.action.MAIN";
    public const string SendAction = "android.intent.action.SEND";
    public const string TimerOpenAction = "org.snook.spike.OPEN_TIMER";
    public const string QuickCaptureAction = "org.snook.spike.QUICK_CAPTURE";
    public const string TimeTrackerAction = "org.snook.spike.OPEN_TIME_TRACKER";
    public const string JournalEntryAction = "org.snook.spike.NEW_JOURNAL_ENTRY";
    public const string TodayAction = "org.snook.spike.OPEN_TODAY";
    public const string HabitsAction = "org.snook.spike.OPEN_HABITS";
    public const string PlainTextMime = "text/plain";
    public const int MaximumSharedTextLength = 300;
    public const int TimerTokenLength = 64;

    public static AndroidEntryRoute Route(AndroidEntryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Action is null or MainAction)
        {
            return HasNoPayload(request)
                ? new(AndroidEntryKind.NormalLaunch)
                : new(AndroidEntryKind.Rejected);
        }

        if (request.Action == SendAction)
        {
            if (request.MimeType != PlainTextMime || HasData(request)
                || request.HasStream || request.HasClipData
                || request.ExtraCount != 1 || !request.HasTextExtra
                || request.HasTokenExtra || !ValidSharedText(request.Text))
                return new(AndroidEntryKind.RejectedShare);
            return new(AndroidEntryKind.SharedText, request.Text!.Trim());
        }

        if (request.Action == TimerOpenAction)
        {
            var token = request.Text;
            if (request.MimeType is not null || request.HasStream || request.HasClipData
                || request.ExtraCount != 1 || request.HasTextExtra || !request.HasTokenExtra
                || request.DataScheme != "snook-internal" || request.DataHost != "timer"
                || request.DataPath != "/" + token || request.DataQuery is not null
                || request.DataFragment is not null || request.DataPort != -1
                || !ValidToken(token))
                return new(AndroidEntryKind.Rejected);
            return new(AndroidEntryKind.TimerOpen, token);
        }

        if (request.Action is QuickCaptureAction or TimeTrackerAction
            or JournalEntryAction or TodayAction or HabitsAction)
        {
            if (!HasNoPayload(request)) return new(AndroidEntryKind.Rejected);
            return new(request.Action switch
            {
                QuickCaptureAction => AndroidEntryKind.QuickCapture,
                TimeTrackerAction => AndroidEntryKind.TimeTracker,
                JournalEntryAction => AndroidEntryKind.JournalEntry,
                TodayAction => AndroidEntryKind.Today,
                _ => AndroidEntryKind.Habits
            });
        }

        return new(AndroidEntryKind.Rejected);
    }

    private static bool HasNoPayload(AndroidEntryRequest request) => request.MimeType is null
        && !HasData(request) && request.Text is null && request.ExtraCount == 0
        && !request.HasTextExtra && !request.HasTokenExtra && !request.HasStream
        && !request.HasClipData;

    private static bool HasData(AndroidEntryRequest request) => request.DataScheme is not null
        || request.DataHost is not null || request.DataPath is not null
        || request.DataQuery is not null || request.DataFragment is not null
        || request.DataPort != -1;

    private static bool ValidSharedText(string? text) => text is not null
        && text.Length <= MaximumSharedTextLength && !string.IsNullOrWhiteSpace(text)
        && !text.Any(character => char.IsControl(character)
            && character is not '\r' and not '\n' and not '\t');

    private static bool ValidToken(string? token) => token?.Length == TimerTokenLength
        && token.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');
}
