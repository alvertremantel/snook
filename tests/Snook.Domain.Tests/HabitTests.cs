using Snook.Domain;
using Xunit;

namespace Snook.Domain.Tests;

public sealed class HabitTests
{
    [Theory]
    [InlineData("2026-03-08T05:59:00Z", "2026-03-07")]
    [InlineData("2026-03-08T08:01:00Z", "2026-03-08")]
    [InlineData("2026-11-01T06:30:00Z", "2026-11-01")]
    [InlineData("2026-11-01T07:30:00Z", "2026-11-01")]
    public void HabitDaysFollowTheirZoneAcrossMidnightAndDst(string instant, string day)
        => Assert.Equal(DateOnly.Parse(day, System.Globalization.CultureInfo.InvariantCulture),
            HabitRules.Today(DateTimeOffset.Parse(instant, System.Globalization.CultureInfo.InvariantCulture), "America/Chicago"));

    [Fact]
    public void CorrectionsHaveInclusiveCivilDateBounds()
    {
        var today = new DateOnly(2026, 9, 18);
        var start = today.AddDays(-365);
        HabitRules.ValidateDate(start, start, today);
        HabitRules.ValidateDate(today, start, today);
        Assert.Throws<SnookException>(() => HabitRules.ValidateDate(start.AddDays(-1), start, today));
        Assert.Throws<SnookException>(() => HabitRules.ValidateDate(today.AddDays(1), start, today));
        Assert.Throws<SnookException>(() => HabitRules.ValidateDate(start, today, today));
        Assert.Throws<SnookException>(() => HabitRules.ResolveZone("not/a/time-zone"));
    }
}
