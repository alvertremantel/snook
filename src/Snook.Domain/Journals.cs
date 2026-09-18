namespace Snook.Domain;

public sealed record Journal(Guid Id, string Name, string Description, DateTimeOffset CreatedAtUtc,
    DateTimeOffset? DeletedAtUtc, long Revision);

public sealed record JournalDefinition(string Name, string Description = "");

public sealed record JournalEntry(Guid Id, Guid JournalId, string Title, string Content,
    DateTimeOffset OccurredAtUtc, int? Mood, IReadOnlyList<string> Tags,
    DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, DateTimeOffset? DeletedAtUtc, long Revision);

public sealed record JournalEntryDefinition(Guid JournalId, string Title, string Content,
    DateTimeOffset OccurredAtUtc, int? Mood = null, IReadOnlyList<string>? Tags = null);

public static class JournalRules
{
    public const int MaximumJournals = 200;
    public const int MaximumContentLength = 20000;

    public static JournalDefinition Validate(JournalDefinition definition)
    {
        if (definition is null || string.IsNullOrWhiteSpace(definition.Name) || definition.Name.Length > 100
            || definition.Description is null || definition.Description.Length > 1000)
            throw Invalid("Enter a journal name of 1–100 characters and a description of at most 1000 characters.");
        return definition with { Name = definition.Name.Trim(), Description = definition.Description.Trim() };
    }

    public static JournalEntryDefinition Validate(JournalEntryDefinition definition)
    {
        if (definition is null || definition.JournalId == Guid.Empty || definition.Title is null || definition.Title.Length > 200
            || string.IsNullOrWhiteSpace(definition.Content) || definition.Content.Length > MaximumContentLength
            || definition.Mood is < 1 or > 7 || definition.OccurredAtUtc.Year < 1900)
            throw Invalid("Choose a journal, enter an entry of 1–20,000 characters, a title of at most 200 characters, a date on or after 1900, and an optional mood from 1 to 7.");
        var tags = definition.Tags ?? [];
        if (tags.Count > 20 || tags.Any(tag => string.IsNullOrWhiteSpace(tag) || tag.Length > 50 || tag.Contains(',', StringComparison.Ordinal)))
            throw Invalid("Use at most 20 journal tags, each 1–50 characters without commas.");
        return definition with
        {
            Title = definition.Title.Trim(), Content = definition.Content.Trim(),
            OccurredAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(definition.OccurredAtUtc.ToUnixTimeMilliseconds()),
            Tags = tags.Select(tag => tag.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray()
        };
    }

    private static SnookException Invalid(string message) => new(SnookErrorCode.ValidationFailed, message);
}
