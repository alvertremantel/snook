using Snook.Domain;
using Xunit;

namespace Snook.Domain.Tests;

public sealed class TimeMathTests
{
    [Theory]
    [InlineData(3, 8, 23)]
    [InlineData(11, 1, 25)]
    [InlineData(9, 12, 24)]
    public void LocalDayUsesBothMidnightOffsets(int month, int day, int hours)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
        var instant = new DateTimeOffset(2026, month, day, 18, 0, 0, TimeSpan.Zero);
        var range = TimeMath.LocalDayRangeUtc(instant, zone);
        Assert.Equal(TimeSpan.FromHours(hours), range.End - range.Start);
        Assert.Equal(TimeSpan.Zero, TimeZoneInfo.ConvertTime(range.Start, zone).TimeOfDay);
        Assert.Equal(TimeSpan.Zero, TimeZoneInfo.ConvertTime(range.End, zone).TimeOfDay);
    }

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
