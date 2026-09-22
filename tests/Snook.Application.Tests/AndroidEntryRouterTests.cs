using Snook.Application;
using Xunit;

namespace Snook.Application.Tests;

public sealed class AndroidEntryRouterTests
{
    private const string Token = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

    [Theory]
    [InlineData(null)]
    [InlineData(AndroidEntryRouter.MainAction)]
    public void PlainComponentAndLauncherEntriesAreNormal(string? action)
    {
        Assert.Equal(AndroidEntryKind.NormalLaunch, AndroidEntryRouter.Route(Empty(action)).Kind);
    }

    [Fact]
    public void PlainTextShareIsTrimmedAndBounded()
    {
        var route = AndroidEntryRouter.Route(Empty(AndroidEntryRouter.SendAction) with
        {
            MimeType = AndroidEntryRouter.PlainTextMime,
            Text = " shared task ",
            ExtraCount = 1,
            HasTextExtra = true
        });
        Assert.Equal(new AndroidEntryRoute(AndroidEntryKind.SharedText, "shared task"), route);
        Assert.Equal(AndroidEntryKind.SharedText, AndroidEntryRouter.Route(Empty(AndroidEntryRouter.SendAction) with
        {
            MimeType = AndroidEntryRouter.PlainTextMime,
            Text = new string('x', AndroidEntryRouter.MaximumSharedTextLength),
            ExtraCount = 1,
            HasTextExtra = true
        }).Kind);
        Assert.Equal(AndroidEntryKind.RejectedShare, AndroidEntryRouter.Route(Empty(AndroidEntryRouter.SendAction) with
        {
            MimeType = AndroidEntryRouter.PlainTextMime,
            Text = new string('x', AndroidEntryRouter.MaximumSharedTextLength + 1),
            ExtraCount = 1,
            HasTextExtra = true
        }).Kind);
    }

    [Theory]
    [InlineData("text/html", null, false, false, 1)]
    [InlineData(AndroidEntryRouter.PlainTextMime, "file", false, false, 1)]
    [InlineData(AndroidEntryRouter.PlainTextMime, null, true, false, 2)]
    [InlineData(AndroidEntryRouter.PlainTextMime, null, false, true, 1)]
    [InlineData(AndroidEntryRouter.PlainTextMime, null, false, false, 2)]
    public void ShareRejectsAlternateTypesUrisStreamsClipsAndExtraKeys(
        string mime, string? scheme, bool stream, bool clip, int extras)
    {
        var route = AndroidEntryRouter.Route(Empty(AndroidEntryRouter.SendAction) with
        {
            MimeType = mime,
            DataScheme = scheme,
            Text = "task",
            ExtraCount = extras,
            HasTextExtra = true,
            HasStream = stream,
            HasClipData = clip
        });
        Assert.Equal(AndroidEntryKind.RejectedShare, route.Kind);
    }

    [Fact]
    public void ExactTimerDestinationRoutesOpaqueToken()
    {
        var route = AndroidEntryRouter.Route(Timer(Token));
        Assert.Equal(new AndroidEntryRoute(AndroidEntryKind.TimerOpen, Token), route);
    }

    [Theory]
    [InlineData("0123")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("G123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDE")]
    public void TimerRejectsMalformedTokens(string token)
    {
        Assert.Equal(AndroidEntryKind.Rejected, AndroidEntryRouter.Route(Timer(token)).Kind);
    }

    [Fact]
    public void TimerRejectsPathSubstitutionQueryFragmentPortAndUnexpectedExtras()
    {
        var valid = Timer(Token);
        AssertRejected(valid with { DataPath = "/" + new string('A', 64) });
        AssertRejected(valid with { DataQuery = "retry=true" });
        AssertRejected(valid with { DataFragment = "open" });
        AssertRejected(valid with { DataPort = 443 });
        AssertRejected(valid with { ExtraCount = 2 });
        AssertRejected(valid with { HasTextExtra = true });
    }

    [Fact]
    public void UnknownOrPayloadBearingMainEntryFailsClosed()
    {
        AssertRejected(Empty("org.example.UNKNOWN"));
        AssertRejected(Empty(AndroidEntryRouter.MainAction) with { ExtraCount = 1 });
        AssertRejected(Empty(null) with { DataScheme = "https", DataHost = "example.test" });
    }

    [Theory]
    [InlineData(AndroidEntryRouter.QuickCaptureAction, AndroidEntryKind.QuickCapture)]
    [InlineData(AndroidEntryRouter.TimeTrackerAction, AndroidEntryKind.TimeTracker)]
    [InlineData(AndroidEntryRouter.JournalEntryAction, AndroidEntryKind.JournalEntry)]
    [InlineData(AndroidEntryRouter.TodayAction, AndroidEntryKind.Today)]
    [InlineData(AndroidEntryRouter.HabitsAction, AndroidEntryKind.Habits)]
    public void ExactShortcutActionsRouteWithoutPayload(string action, AndroidEntryKind kind)
    {
        Assert.Equal(kind, AndroidEntryRouter.Route(Empty(action)).Kind);
        AssertRejected(Empty(action) with { ExtraCount = 1 });
        AssertRejected(Empty(action) with { DataScheme = "snook", DataPath = "/shortcut" });
    }

    private static void AssertRejected(AndroidEntryRequest request)
        => Assert.Equal(AndroidEntryKind.Rejected, AndroidEntryRouter.Route(request).Kind);

    private static AndroidEntryRequest Timer(string token) => Empty(AndroidEntryRouter.TimerOpenAction) with
    {
        DataScheme = "snook-internal",
        DataHost = "timer",
        DataPath = "/" + token,
        Text = token,
        ExtraCount = 1,
        HasTokenExtra = true
    };

    private static AndroidEntryRequest Empty(string? action) => new(action, null, null, null,
        null, null, null, -1, null, 0, false, false, false, false);
}
