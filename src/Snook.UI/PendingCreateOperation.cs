using System.Text.Json;
using Snook.Contracts;
using Snook.Domain;

namespace Snook.UI;

/// <summary>A UI draft owns one attempt until success, changed intent, or explicit discard.</summary>
internal sealed class PendingCreateOperation
{
    private readonly Guid _deviceId = Guid.NewGuid();
    private Attempt? _pending;
    private long _generation;

    public OperationRequest? RequestFor(object intent)
        => _pending is { } attempt && attempt.Fingerprint == JsonSerializer.Serialize(intent) ? attempt.Request : null;

    public void Restore<TArguments>(TArguments arguments, OperationRequest request) where TArguments : notnull
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.OperationId == Guid.Empty || request.ClientDeviceId == Guid.Empty || request.ExpectedRevision is not null)
            throw new ArgumentException("Invalid creation recovery request.", nameof(request));
        Reset();
        _pending = new Attempt(JsonSerializer.Serialize(arguments), arguments, request);
    }

    public void RestoreCaptured<TArguments>(object intent, TArguments arguments,
        OperationRequest request) where TArguments : notnull
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(request);
        if (request.OperationId == Guid.Empty || request.ClientDeviceId == Guid.Empty
            || request.ExpectedRevision is not null)
            throw new ArgumentException("Invalid creation recovery request.", nameof(request));
        Reset();
        _pending = new Attempt(JsonSerializer.Serialize(intent), arguments, request);
    }

    public void RestoreIntent(object intent, OperationRequest request)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(request);
        if (request.OperationId == Guid.Empty || request.ClientDeviceId == Guid.Empty
            || request.ExpectedRevision is not null)
            throw new ArgumentException("Invalid creation recovery request.", nameof(request));
        Reset();
        _pending = new Attempt(JsonSerializer.Serialize(intent), null, request);
    }

    public Task<TResult> RunAsync<TArguments, TResult>(Func<TArguments> read,
        Func<TArguments, OperationRequest, Task<TResult>> send,
        Func<Task>? checkpoint = null) where TArguments : notnull
    {
        var arguments = read();
        return RunCapturedAsync(arguments, () => Task.FromResult(arguments), send, () => read(), checkpoint);
    }

    public async Task RunAsync<TArguments>(Func<TArguments> read,
        Func<TArguments, OperationRequest, Task> send,
        Func<Task>? checkpoint = null) where TArguments : notnull
        => await RunAsync(read, async (captured, request) =>
        {
            await send(captured, request);
            return true;
        }, checkpoint);

    public async Task<TResult> RunCapturedAsync<TArguments, TResult>(object intent,
        Func<Task<TArguments>> capture, Func<TArguments, OperationRequest, Task<TResult>> send,
        Func<object>? readIntent = null, Func<Task>? checkpoint = null,
        Action<TArguments>? captured = null) where TArguments : notnull
    {
        var fingerprint = JsonSerializer.Serialize(intent);
        if (_pending is null || _pending.Fingerprint != fingerprint)
        {
            // Freeze derived timestamps/defaults before dispatch. Failed capture has
            // not sent a mutation; an uncertain send keeps this exact attempt.
            var generation = _generation;
            var arguments = await capture();
            if (generation != _generation || readIntent is not null && JsonSerializer.Serialize(readIntent()) != fingerprint)
                throw new OperationCanceledException("The creation draft changed or was discarded before sending. No creation was sent.");
            _pending = new Attempt(fingerprint, arguments, new OperationRequest(Guid.NewGuid(), _deviceId));
        }
        else if (_pending.Arguments is null)
        {
            var recoveredAttempt = _pending;
            var generation = _generation;
            var arguments = await capture();
            if (generation != _generation || !ReferenceEquals(_pending, recoveredAttempt)
                || readIntent is not null && JsonSerializer.Serialize(readIntent()) != fingerprint)
                throw new OperationCanceledException("The recovered creation draft changed before retrying. No creation was sent.");
            _pending = recoveredAttempt with { Arguments = arguments };
        }
        var attempt = _pending;
        captured?.Invoke((TArguments)attempt.Arguments!);
        if (checkpoint is not null) await checkpoint();
        TResult result;
        try
        {
            result = await send((TArguments)attempt.Arguments!, attempt.Request);
        }
        catch (SnookException exception) when (exception.Code == SnookErrorCode.StoreUnavailable
            && (!ReferenceEquals(_pending, attempt) || readIntent is not null && JsonSerializer.Serialize(readIntent()) != fingerprint))
        {
            // The original inputs are no longer on screen. Retrying the current
            // form would be a new write, not recovery of the uncertain request.
            throw new OperationCanceledException("The earlier creation could not be confirmed. Your current draft was not changed. Refresh and inspect saved data before creating again.", exception);
        }
        // A post-save refresh failure must not retain an already-confirmed create.
        // An explicit Cancel while awaiting also must not revive the old attempt.
        if (!ReferenceEquals(_pending, attempt) || readIntent is not null && JsonSerializer.Serialize(readIntent()) != fingerprint)
        {
            if (ReferenceEquals(_pending, attempt)) Reset();
            throw new OperationCanceledException("The earlier creation was saved. Your current draft was not changed. Refresh to view the saved item.");
        }
        Reset();
        return result;
    }

    public void Reset()
    {
        _pending = null;
        _generation++;
    }

    private sealed record Attempt(string Fingerprint, object? Arguments, OperationRequest Request);

    public static string Failure(Exception exception, string fallback) => exception switch
    {
        OperationCanceledException => exception.Message,
        SnookException { Code: SnookErrorCode.StoreUnavailable } =>
            $"{exception.Message} Retry unchanged to confirm or complete this creation. Changing inputs starts a new operation.",
        SnookException => exception.Message,
        _ => fallback
    };
}
