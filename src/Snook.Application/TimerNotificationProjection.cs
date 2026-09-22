using System.Globalization;
using Snook.Contracts;
using Snook.Domain;

namespace Snook.Application;

public sealed record TimerNotificationItem(
    Guid SessionId,
    long Revision,
    SessionState State,
    SessionLane Lane,
    string Title,
    string Context,
    long DisplayedMilliseconds,
    DateTimeOffset StartedAtUtc);

public sealed record TimerNotificationSnapshot(
    Guid WorkspaceId,
    DateTimeOffset CapturedAtUtc,
    IReadOnlyList<TimerNotificationItem> Items,
    int TotalActiveCount);

/// <summary>
/// Produces a bounded, privacy-conscious native timer projection from one committed
/// bootstrap snapshot. Native adapters remain responsible for permission, channel,
/// PendingIntent and lock-screen policy.
/// </summary>
public static class TimerNotificationProjection
{
    public const int DefaultMaximumItems = 16;
    private const int MaximumTextElements = 160;
    private const long MaximumDisplayedMilliseconds = 3_155_760_000_000; // 100 years

    public static TimerNotificationSnapshot Build(BootstrapSnapshot snapshot, int maximumItems = DefaultMaximumItems)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (maximumItems is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(maximumItems));

        var active = snapshot.Today.ActiveSessions
            .Where(item => item.Session.State is SessionState.Running or SessionState.Paused)
            .OrderBy(item => item.Session.Lane)
            .ThenBy(item => item.Session.State == SessionState.Running ? 0 : 1)
            .ThenBy(item => item.Session.StartedAtUtc)
            .ThenBy(item => item.Session.Id)
            .ToArray();
        var projected = active.Take(maximumItems).Select(Project).ToArray();
        return new TimerNotificationSnapshot(snapshot.Workspace.Id, snapshot.Today.CapturedAtUtc,
            projected, active.Length);
    }

    private static TimerNotificationItem Project(ActiveSessionItem item)
    {
        var task = Normalize(item.TaskTitle);
        var activity = Normalize(item.ActivityName);
        var lane = item.Session.Lane == SessionLane.Background ? "Background" : "Focus";
        var title = task ?? activity ?? "Untitled timer";
        var context = task is not null && activity is not null ? activity : lane;
        return new TimerNotificationItem(item.Session.Id, item.Session.Revision, item.Session.State,
            item.Session.Lane, title, context,
            Math.Clamp(item.DisplayedMilliseconds, 0, MaximumDisplayedMilliseconds),
            item.Session.StartedAtUtc);
    }

    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim();
        var elements = StringInfo.ParseCombiningCharacters(normalized);
        if (elements.Length <= MaximumTextElements) return normalized;
        return normalized[..elements[MaximumTextElements]] + "…";
    }
}
