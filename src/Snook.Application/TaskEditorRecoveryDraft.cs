using System.Text.Json;
using System.Text.Json.Serialization;
using Snook.Domain;

namespace Snook.Application;

/// <summary>
/// Bounded recovery metadata for one unsaved task editor. It is device-local UI
/// state, not workspace state, and never authorizes a mutation by itself.
/// </summary>
public sealed record TaskEditorRecoveryDraft(
    int Version,
    Guid WorkspaceId,
    DateTimeOffset SavedAtUtc,
    Guid TaskId,
    long ExpectedRevision,
    string DraftTitle,
    string DraftDescription,
    Priority DraftPriority,
    string DueDateText,
    Guid? DraftActivityId,
    bool DraftStarred,
    Guid TargetProjectId,
    string TagText,
    Guid? PrerequisiteTaskId,
    string LinkLabelText,
    string LinkUriText,
    string ReminderTimeText = "",
    string ReminderTimeZone = "",
    AmbiguousTimeChoice ReminderFoldChoice = AmbiguousTimeChoice.Earlier)
{
    public const int MaximumEncodedBytes = 32 * 1024;

    public bool IsValid(Guid workspaceId, DateTimeOffset now) => Version is 1 or 2
        && workspaceId != Guid.Empty && WorkspaceId == workspaceId
        && SavedAtUtc.Offset == TimeSpan.Zero && SavedAtUtc <= now
        && now - SavedAtUtc < TimeSpan.FromDays(7)
        && TaskId != Guid.Empty && ExpectedRevision > 0
        && DraftTitle is not null && DraftTitle.Length <= 300
        && DraftDescription is not null && DraftDescription.Length <= 20_000
        && Enum.IsDefined(DraftPriority)
        && DueDateText is not null && DueDateText.Length <= 64
        && (DraftActivityId is null || DraftActivityId != Guid.Empty)
        && TargetProjectId != Guid.Empty
        && TagText is not null && TagText.Length <= 300
        && (PrerequisiteTaskId is null || PrerequisiteTaskId != Guid.Empty)
        && LinkLabelText is not null && LinkLabelText.Length <= 300
        && LinkUriText is not null && LinkUriText.Length <= 4096
        && ReminderTimeText is not null && ReminderTimeText.Length <= 16
        && ReminderTimeZone is not null && ReminderTimeZone.Length <= 256
        && Enum.IsDefined(ReminderFoldChoice)
        && (Version == 1 || ReminderTimeZone.Length > 0);

    public byte[] Encode()
    {
        if (!IsValid(WorkspaceId, SavedAtUtc))
            throw new InvalidDataException("Invalid task-editor recovery data.");
        var data = JsonSerializer.SerializeToUtf8Bytes(this,
            TaskEditorRecoveryDraftJson.Default.TaskEditorRecoveryDraft);
        if (data.Length > MaximumEncodedBytes)
            throw new InvalidDataException("Task-editor recovery data exceeds its size limit.");
        return data;
    }

    public static TaskEditorRecoveryDraft? Decode(ReadOnlySpan<byte> data, Guid workspaceId,
        DateTimeOffset now)
    {
        if (data.Length is < 1 or > MaximumEncodedBytes) return null;
        try
        {
            var draft = JsonSerializer.Deserialize(data,
                TaskEditorRecoveryDraftJson.Default.TaskEditorRecoveryDraft);
            return draft?.IsValid(workspaceId, now) == true ? draft : null;
        }
        catch (JsonException) { return null; }
    }
}

[JsonSerializable(typeof(TaskEditorRecoveryDraft))]
internal sealed partial class TaskEditorRecoveryDraftJson : JsonSerializerContext;
