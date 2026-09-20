using System.Globalization;
using Snook.Contracts;
using Snook.Domain;

namespace Snook.Application;

public sealed record WorkspaceWidgetTask(Guid TaskId, string Title, string Context);
public sealed record WorkspaceWidgetHabit(Guid HabitId, string Name, bool CompletedToday, int CurrentStreak);
public sealed record WorkspaceWidgetSnapshot(
    Guid WorkspaceId,
    DateTimeOffset CapturedAtUtc,
    IReadOnlyList<WorkspaceWidgetTask> NextTasks,
    int OpenTaskCount,
    IReadOnlyList<WorkspaceWidgetHabit> Habits);

/// <summary>
/// Builds the bounded, read-only projection used by Android home-screen widgets.
/// It contains no descriptions, journal text, credentials, paths, or mutation data.
/// </summary>
public static class WorkspaceWidgetProjection
{
    public const int DefaultMaximumItems = 3;
    private const int MaximumTextElements = 80;

    public static WorkspaceWidgetSnapshot Build(BootstrapSnapshot snapshot,
        IReadOnlyList<HabitProgress>? habits = null, int maximumItems = DefaultMaximumItems)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (maximumItems is < 1 or > 6) throw new ArgumentOutOfRangeException(nameof(maximumItems));

        var tasks = snapshot.Today.PriorityTasks
            .Where(item => item.Task.Status == TaskState.Open
                && item.Task.ArchivedAtUtc is null && item.Task.DeletedAtUtc is null)
            .Take(maximumItems)
            .Select(item => new WorkspaceWidgetTask(item.Task.Id, Normalize(item.Task.Title),
                Context(item.BoardName, item.ProjectName)))
            .ToArray();
        var projectedHabits = (habits ?? [])
            .Where(item => item.Habit.ArchivedAtUtc is null && item.Habit.DeletedAtUtc is null)
            .OrderBy(item => item.Habit.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Habit.Id)
            .Take(maximumItems)
            .Select(item => new WorkspaceWidgetHabit(item.Habit.Id, Normalize(item.Habit.Name),
                item.CheckIns.Any(checkIn => checkIn.Date == item.Today),
                Math.Max(0, item.CurrentStreak)))
            .ToArray();

        return new WorkspaceWidgetSnapshot(snapshot.Workspace.Id, snapshot.Today.CapturedAtUtc,
            tasks, Math.Max(0, snapshot.Today.OpenTaskCount), projectedHabits);
    }

    private static string Normalize(string value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? "Untitled" : value.Trim();
        var elements = StringInfo.ParseCombiningCharacters(normalized);
        return elements.Length <= MaximumTextElements
            ? normalized : normalized[..elements[MaximumTextElements]] + "…";
    }

    private static string Context(string board, string project)
    {
        var parts = new[] { board, project }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToArray();
        return parts.Length == 0 ? "Untitled" : Normalize(string.Join(" / ", parts));
    }
}
