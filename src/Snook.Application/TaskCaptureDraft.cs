using System.Text.Json;
using System.Text.Json.Serialization;
using Snook.Contracts;

namespace Snook.Application;

public sealed record TaskCaptureIntent(Guid ProjectId, string Title);

/// <summary>Recovery data, not workspace state. A pending request preserves exact retry identity.</summary>
public sealed record TaskCaptureDraft(int Version, Guid WorkspaceId, DateTimeOffset SavedAtUtc,
    TaskCaptureIntent Intent, OperationRequest? PendingRequest)
{
    public bool IsValid(Guid workspaceId, DateTimeOffset now) => Version == 1
        && workspaceId != Guid.Empty && WorkspaceId == workspaceId
        && SavedAtUtc.Offset == TimeSpan.Zero && SavedAtUtc <= now && now - SavedAtUtc < TimeSpan.FromDays(7)
        && Intent is { Title: not null } && Intent.ProjectId != Guid.Empty
        && Intent.Title.Length is > 0 and <= 300 && !string.IsNullOrWhiteSpace(Intent.Title)
        && (PendingRequest is null || PendingRequest.OperationId != Guid.Empty
            && PendingRequest.ClientDeviceId != Guid.Empty && PendingRequest.ExpectedRevision is null);

    public byte[] Encode()
    {
        if (!IsValid(WorkspaceId, SavedAtUtc)) throw new InvalidDataException("Invalid capture recovery data.");
        var data = JsonSerializer.SerializeToUtf8Bytes(this, TaskCaptureDraftJson.Default.TaskCaptureDraft);
        if (data.Length > 4096) throw new InvalidDataException("Capture recovery data exceeds its size limit.");
        return data;
    }

    public static TaskCaptureDraft? Decode(ReadOnlySpan<byte> data, Guid workspaceId, DateTimeOffset now)
    {
        if (data.Length is < 1 or > 4096) return null;
        try
        {
            var draft = JsonSerializer.Deserialize(data, TaskCaptureDraftJson.Default.TaskCaptureDraft);
            return draft?.IsValid(workspaceId, now) == true ? draft : null;
        }
        catch (JsonException) { return null; }
    }
}

[JsonSerializable(typeof(TaskCaptureDraft))]
internal sealed partial class TaskCaptureDraftJson : JsonSerializerContext;
