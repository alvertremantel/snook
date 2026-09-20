using Xunit;

namespace Snook.Application.Tests;

public sealed class SharedCaptureInboxTests
{
    [Theory]
    [InlineData("android.intent.action.VIEW", "text/plain", null, "hello")]
    [InlineData("android.intent.action.SEND", "text/html", null, "hello")]
    [InlineData("android.intent.action.SEND", "text/plain", "file:///private", "hello")]
    [InlineData("android.intent.action.SEND", "text/plain", null, " ")]
    [InlineData("android.intent.action.SEND", "text/plain", null, "bad\0text")]
    public void InvalidPayloadDoesNotEnterInbox(string action, string mime, string? uri, string text)
    {
        var inbox = new SharedCaptureInbox();
        Assert.False(inbox.TryOffer(action, mime, uri, text));
        Assert.Null(inbox.Text);
    }

    [Fact]
    public void InboxBoundsContentAndDoesNotReplacePendingShare()
    {
        var inbox = new SharedCaptureInbox();
        Assert.False(inbox.TryOffer("android.intent.action.SEND", "text/plain", null, new string('x', 301)));
        Assert.True(inbox.TryOffer("android.intent.action.SEND", "text/plain", null, " https://example.com "));
        Assert.False(inbox.TryOffer("android.intent.action.SEND", "text/plain", null, "replacement"));
        Assert.Equal("https://example.com", inbox.Text);
        inbox.Discard();
        Assert.Null(inbox.Text);
        Assert.True(inbox.TryOffer("android.intent.action.SEND", "text/plain", null, new string('x', 300)));
    }

    [Fact]
    public void UnreviewedShareExpires()
    {
        var clock = new ShareClock();
        var inbox = new SharedCaptureInbox(clock);
        Assert.True(inbox.TryOffer("android.intent.action.SEND", "text/plain", null, "private draft"));
        clock.Now = clock.Now.AddMinutes(10);
        Assert.Null(inbox.Text);
        Assert.True(inbox.TryOffer("android.intent.action.SEND", "text/plain", null, "new share"));
    }

    private sealed class ShareClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
