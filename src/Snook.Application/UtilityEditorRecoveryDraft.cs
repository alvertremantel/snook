using System.Text.Json;
using System.Text.Json.Serialization;
using Snook.Contracts;
using Snook.Domain;

namespace Snook.Application;

/// <summary>
/// Bounded, device-local recovery state for one non-task workspace editor. Values
/// are an explicit per-kind field map interpreted by the shared UI; this record is
/// not workspace state and never authorizes a mutation without current backend
/// validation.
/// </summary>
public sealed record UtilityEditorRecoveryDraft(
    int Version,
    Guid WorkspaceId,
    DateTimeOffset SavedAtUtc,
    string Kind,
    Guid? EntityId,
    long? ExpectedRevision,
    IReadOnlyDictionary<string, string?> Values,
    IReadOnlyList<TaskRevision> TargetRevisions,
    OperationRequest? PendingRequest)
{
    public const int MaximumEncodedBytes = 64 * 1024;
    public const int MaximumFields = 40;
    public const int MaximumTargets = 500;
    private static readonly HashSet<string> Kinds =
    [
        "manual", "activity", "plan", "event", "board", "project",
        "activity-group", "activity-edit", "calendar", "history", "block",
        "event-edit", "journal", "journal-entry", "habit", "bulk"
    ];

    public bool IsValid(Guid workspaceId, DateTimeOffset now)
    {
        if (Version != 1 || WorkspaceId == Guid.Empty || WorkspaceId != workspaceId
            || SavedAtUtc.Offset != TimeSpan.Zero || SavedAtUtc > now
            || now - SavedAtUtc >= TimeSpan.FromDays(7)
            || !Kinds.Contains(Kind) || Values is null || Values.Count > MaximumFields
            || TargetRevisions is null || TargetRevisions.Count > MaximumTargets
            || TargetRevisions.Any(target => target.TaskId == Guid.Empty || target.ExpectedRevision <= 0)
            || TargetRevisions.Select(target => target.TaskId).Distinct().Count() != TargetRevisions.Count)
            return false;
        var totalCharacters = 0;
        foreach (var (key, value) in Values)
        {
            if (string.IsNullOrEmpty(key) || key.Length > 64
                || key.Any(character => char.IsControl(character))
                || value is { Length: > 24_000 }) return false;
            totalCharacters += key.Length + (value?.Length ?? 0);
            if (totalCharacters > 48_000) return false;
        }
        if (EntityId == Guid.Empty || ExpectedRevision is <= 0) return false;
        if ((EntityId is null) != (ExpectedRevision is null)) return false;
        if (Kind == "bulk" && TargetRevisions.Count == 0) return false;
        if (Kind != "bulk" && TargetRevisions.Count != 0) return false;
        if (PendingRequest is { } request
            && (request.OperationId == Guid.Empty || request.ClientDeviceId == Guid.Empty
                || request.ExpectedRevision != ExpectedRevision)) return false;
        return true;
    }

    public byte[] Encode()
    {
        if (!IsValid(WorkspaceId, SavedAtUtc))
            throw new InvalidDataException("Invalid utility-editor recovery data.");
        var data = JsonSerializer.SerializeToUtf8Bytes(this,
            UtilityEditorRecoveryDraftJson.Default.UtilityEditorRecoveryDraft);
        if (data.Length > MaximumEncodedBytes)
            throw new InvalidDataException("Utility-editor recovery data exceeds its size limit.");
        return data;
    }

    public static UtilityEditorRecoveryDraft? Decode(ReadOnlySpan<byte> data,
        Guid workspaceId, DateTimeOffset now)
    {
        if (data.Length is < 1 or > MaximumEncodedBytes) return null;
        try
        {
            var draft = JsonSerializer.Deserialize(data,
                UtilityEditorRecoveryDraftJson.Default.UtilityEditorRecoveryDraft);
            return draft?.IsValid(workspaceId, now) == true ? draft : null;
        }
        catch (JsonException) { return null; }
    }
}

[JsonSerializable(typeof(UtilityEditorRecoveryDraft))]
internal sealed partial class UtilityEditorRecoveryDraftJson : JsonSerializerContext;
