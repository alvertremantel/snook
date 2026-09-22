using System.Text.Json;
using System.Text.Json.Serialization;

namespace Snook.Application;

/// <summary>
/// Bounded, non-secret Android presentation metadata. It identifies no backend
/// object other than optional workspace-scoped selection IDs and never carries a
/// draft, search term, credential, or mutation request.
/// </summary>
public sealed record WorkspaceNavigationState(
    int Version,
    Guid WorkspaceId,
    string Section,
    string TaskView,
    Guid TaskBoardId,
    string TaskSort,
    string CalendarMode,
    string EventCalendarView,
    string HistoryCalendarView,
    DateOnly CalendarAnchor,
    string SummaryGrouping,
    string SettingsPage,
    Guid SelectedJournalId)
{
    public const int MaximumEncodedBytes = 2048;

    private static readonly string[] Sections =
        ["Today", "Tasks", "Habits", "Journals", "RSS", "Time Tracker", "Calendar", "History", "Summary", "Settings"];
    private static readonly string[] TaskViews = ["List", "Board"];
    private static readonly string[] TaskSorts = ["Priority", "DueDate", "Tracked", "Title"];
    private static readonly string[] CalendarModes = ["Event", "History"];
    private static readonly string[] EventCalendarViews = ["Day", "Week", "Month", "Agenda"];
    private static readonly string[] HistoryCalendarViews = ["Week", "Flex"];
    private static readonly string[] SummaryGroupings = ["Day", "Task", "Project", "Activity", "ActivityGroup", "Tag", "Lane"];
    private static readonly string[] SettingsPages = ["General", "Organization", "Activities", "Calendars"];

    public bool IsValid(Guid workspaceId) => Version == 1
        && workspaceId != Guid.Empty && WorkspaceId == workspaceId
        && Sections.Contains(Section, StringComparer.Ordinal)
        && TaskViews.Contains(TaskView, StringComparer.Ordinal)
        && TaskSorts.Contains(TaskSort, StringComparer.Ordinal)
        && CalendarModes.Contains(CalendarMode, StringComparer.Ordinal)
        && EventCalendarViews.Contains(EventCalendarView, StringComparer.Ordinal)
        && HistoryCalendarViews.Contains(HistoryCalendarView, StringComparer.Ordinal)
        && CalendarAnchor.Year is >= 1900 and <= 9998
        && SummaryGroupings.Contains(SummaryGrouping, StringComparer.Ordinal)
        && SettingsPages.Contains(SettingsPage, StringComparer.Ordinal);

    public byte[] Encode()
    {
        if (!IsValid(WorkspaceId)) throw new InvalidDataException("Invalid navigation state.");
        var data = JsonSerializer.SerializeToUtf8Bytes(this,
            WorkspaceNavigationStateJson.Default.WorkspaceNavigationState);
        if (data.Length > MaximumEncodedBytes)
            throw new InvalidDataException("Navigation state exceeds its size limit.");
        return data;
    }

    public static WorkspaceNavigationState? Decode(ReadOnlySpan<byte> data, Guid workspaceId)
    {
        if (data.Length is < 1 or > MaximumEncodedBytes) return null;
        try
        {
            var state = JsonSerializer.Deserialize(data,
                WorkspaceNavigationStateJson.Default.WorkspaceNavigationState);
            return state?.IsValid(workspaceId) == true ? state : null;
        }
        catch (JsonException) { return null; }
    }
}

[JsonSerializable(typeof(WorkspaceNavigationState))]
internal sealed partial class WorkspaceNavigationStateJson : JsonSerializerContext;
