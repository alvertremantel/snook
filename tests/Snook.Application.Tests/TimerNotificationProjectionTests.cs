using Snook.Application;
using Snook.Contracts;
using Snook.Domain;
using Xunit;

namespace Snook.Application.Tests;

public sealed class TimerNotificationProjectionTests
{
    [Fact]
    public void ProjectionIsCommittedBoundedDeterministicAndPrivacySized()
    {
        var workspace = Guid.NewGuid();
        var captured = new DateTimeOffset(2026, 9, 20, 17, 0, 0, TimeSpan.Zero);
        var foreground = Item(workspace, SessionLane.Foreground, SessionState.Running,
            captured.AddHours(-2), "  Important task  ", "Deep work", -1);
        var paused = Item(workspace, SessionLane.Foreground, SessionState.Paused,
            captured.AddHours(-1), null, "Planning", 42_000);
        var background = Item(workspace, SessionLane.Background, SessionState.Running,
            captured.AddMinutes(-30), new string('x', 200), null, long.MaxValue);
        var stopped = Item(workspace, SessionLane.Foreground, SessionState.Stopped,
            captured.AddMinutes(-10), "Do not publish", null, 10);
        var snapshot = Snapshot(workspace, captured, [background, stopped, paused, foreground]);

        var result = TimerNotificationProjection.Build(snapshot, maximumItems: 2);

        Assert.Equal(workspace, result.WorkspaceId);
        Assert.Equal(captured, result.CapturedAtUtc);
        Assert.Equal(3, result.TotalActiveCount);
        Assert.Collection(result.Items,
            item =>
            {
                Assert.Equal(foreground.Session.Id, item.SessionId);
                Assert.Equal("Important task", item.Title);
                Assert.Equal("Deep work", item.Context);
                Assert.Equal(0, item.DisplayedMilliseconds);
            },
            item =>
            {
                Assert.Equal(paused.Session.Id, item.SessionId);
                Assert.Equal("Planning", item.Title);
                Assert.Equal("Focus", item.Context);
                Assert.Equal(SessionState.Paused, item.State);
            });
        Assert.DoesNotContain(result.Items, item => item.SessionId == stopped.Session.Id);

        var all = TimerNotificationProjection.Build(snapshot).Items;
        Assert.Equal(161, new System.Globalization.StringInfo(all[2].Title).LengthInTextElements);
        Assert.EndsWith("…", all[2].Title, StringComparison.Ordinal);
        Assert.Equal(3_155_760_000_000, all[2].DisplayedMilliseconds);
    }

    [Fact]
    public void ProjectionValidatesBoundsAndUsesAnUntitledFallback()
    {
        var workspace = Guid.NewGuid();
        var captured = DateTimeOffset.UnixEpoch;
        var snapshot = Snapshot(workspace, captured,
            [Item(workspace, SessionLane.Background, SessionState.Running, captured, " ", "\t", 1)]);

        var item = Assert.Single(TimerNotificationProjection.Build(snapshot).Items);
        Assert.Equal("Untitled timer", item.Title);
        Assert.Equal("Background", item.Context);
        Assert.Throws<ArgumentOutOfRangeException>(() => TimerNotificationProjection.Build(snapshot, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => TimerNotificationProjection.Build(snapshot, 33));
    }

    private static ActiveSessionItem Item(Guid workspace, SessionLane lane, SessionState state,
        DateTimeOffset started, string? task, string? activity, long displayed)
    {
        var session = new TrackingSession(Guid.NewGuid(), workspace, task is null ? null : Guid.NewGuid(),
            activity is null ? null : Guid.NewGuid(), lane, state, started,
            state == SessionState.Stopped ? started.AddMinutes(1) : null, "", started, started,
            7, [new TimeInterval(Guid.NewGuid(), started, state == SessionState.Running ? null : started.AddMinutes(1), "test")]);
        return new ActiveSessionItem(session, task, activity, displayed);
    }

    private static BootstrapSnapshot Snapshot(Guid workspace, DateTimeOffset captured,
        IReadOnlyList<ActiveSessionItem> sessions)
        => new(new Workspace(workspace, "Workspace", captured, 1), [], [], [], [],
            new TodaySnapshot(captured, [], sessions, 0, 0), 1,
            new HostCapabilities("Embedded", true, false, true, true, false, false));
}
