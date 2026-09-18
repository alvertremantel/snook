using Snook.Domain;
using Xunit;

namespace Snook.Domain.Tests;

public sealed class JournalTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(7)]
    public void ValidMoodsAndIndependentTagsAreNormalized(int? mood)
    {
        var definition = new JournalEntryDefinition(Guid.NewGuid(), " Title ", " Words\nremain ",
            new DateTimeOffset(2026, 9, 18, 14, 0, 0, TimeSpan.FromHours(2)), mood, [" Ideas ", "ideas", "Gratitude"]);
        var clean = JournalRules.Validate(definition);
        Assert.Equal("Title", clean.Title);
        Assert.Equal("Words\nremain", clean.Content);
        Assert.Equal(2, clean.Tags!.Count);
        Assert.Equal(TimeSpan.Zero, clean.OccurredAtUtc.Offset);
        Assert.Equal(mood, clean.Mood);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(8)]
    public void MoodOutsideOneThroughSevenIsRejected(int mood)
    {
        Assert.Throws<SnookException>(() => JournalRules.Validate(new JournalEntryDefinition(Guid.NewGuid(), "", "Words", DateTimeOffset.UtcNow, mood)));
    }

    [Fact]
    public void BlankContentAndOversizedOrAmbiguousTagsAreRejected()
    {
        var definition = new JournalEntryDefinition(Guid.NewGuid(), "", "Words", DateTimeOffset.UtcNow);
        Assert.Throws<SnookException>(() => JournalRules.Validate(definition with { Content = " " }));
        Assert.Throws<SnookException>(() => JournalRules.Validate(definition with { Tags = ["a,b"] }));
        Assert.Throws<SnookException>(() => JournalRules.Validate(definition with { Tags = [new string('a', 51)] }));
        Assert.Throws<SnookException>(() => JournalRules.Validate(definition with { Tags = [null!] }));
        Assert.Throws<SnookException>(() => JournalRules.Validate(new JournalDefinition(" ")));
        Assert.Throws<SnookException>(() => JournalRules.Validate(new JournalDefinition(new string('a', 101))));
    }
}
