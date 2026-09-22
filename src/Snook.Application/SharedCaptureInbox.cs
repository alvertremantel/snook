namespace Snook.Application;

/// <summary>One bounded, expiring, non-persisted incoming capture awaiting user review.</summary>
public sealed class SharedCaptureInbox(TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private string? _text;
    private DateTimeOffset _expires;

    public string? Text
    {
        get
        {
            if (_clock.GetUtcNow() >= _expires) _text = null;
            return _text;
        }
    }

    public bool TryOffer(string? action, string? mimeType, string? dataUri, string? text)
    {
        if (action != "android.intent.action.SEND" || mimeType != "text/plain" || dataUri is not null
            || text is null || text.Length > 300 || string.IsNullOrWhiteSpace(text)
            || text.Any(character => char.IsControl(character) && character is not '\r' and not '\n' and not '\t'))
            return false;
        if (Text is not null) return false;
        _text = text.Trim();
        _expires = _clock.GetUtcNow().AddMinutes(10);
        return true;
    }

    public void Discard() => _text = null;
}
