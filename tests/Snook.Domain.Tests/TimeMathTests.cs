using Snook.Domain;
using Xunit;

namespace Snook.Domain.Tests;

public sealed class TimeMathTests
{
    [Fact]
    public void OverlapSplitsAnIntervalAtAReportBoundaryWithoutMutatingIt()
    {
        var start = new DateTimeOffset(2026, 9, 8, 23, 30, 0, TimeSpan.Zero);
        var end = start.AddHours(2);
        var interval = new TimeInterval(Guid.NewGuid(), start, end, "timer");

        var firstDayBoundary = new DateTimeOffset(2026, 9, 8, 0, 0, 0, TimeSpan.Zero);
        var secondDayBoundary = firstDayBoundary.AddDays(1);
        var firstDay = TimeMath.OverlapMilliseconds(interval, firstDayBoundary, secondDayBoundary, end);
        var secondDay = TimeMath.OverlapMilliseconds(interval, secondDayBoundary, secondDayBoundary.AddDays(1), end);

        Assert.Equal(TimeSpan.FromMinutes(30).TotalMilliseconds, firstDay);
        Assert.Equal(TimeSpan.FromMinutes(90).TotalMilliseconds, secondDay);
        Assert.Equal(end, interval.EndedAtUtc);
    }

    [Fact]
    public void UnionDurationCountsConcurrentWorkOnce()
    {
        var start = DateTimeOffset.UtcNow;
        var duration = TimeMath.UnionMilliseconds([
            (start, start.AddMinutes(30)),
            (start.AddMinutes(10), start.AddMinutes(40))
        ]);

        Assert.Equal(TimeSpan.FromMinutes(40).TotalMilliseconds, duration);
    }
}
