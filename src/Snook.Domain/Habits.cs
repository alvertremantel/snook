namespace Snook.Domain;

public sealed record Habit(
    Guid Id, string Name, string Description, DateOnly StartDate, string TimeZone,
    DateTimeOffset CreatedAtUtc, DateTimeOffset? ArchivedAtUtc, DateTimeOffset? DeletedAtUtc, long Revision);

public sealed record HabitDefinition(string Name, string Description, DateOnly StartDate, string TimeZone);
public sealed record HabitUpdate(string Name, string Description);
public sealed record HabitCheckIn(Guid HabitId, DateOnly Date, DateTimeOffset CompletedAtUtc);

public sealed record HabitProgress(Habit Habit, DateOnly Today, DateOnly RangeStart, DateOnly RangeEnd,
    IReadOnlyList<HabitCheckIn> CheckIns, int CurrentStreak, int EligibleDays)
{
    public int CompletedDays => CheckIns.Count;
}

public static class HabitRules
{
    public const int MaximumDays = 366;
    public const int MaximumHabits = 500;

    public static TimeZoneInfo ResolveZone(string timeZone)
    {
        if (string.IsNullOrWhiteSpace(timeZone) || timeZone.Length > 200)
            throw new SnookException(SnookErrorCode.ValidationFailed, "Choose a valid habit time zone.");
        var id = timeZone.Trim();
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException) { throw new SnookException(SnookErrorCode.ValidationFailed, "Choose a valid habit time zone."); }
        catch (InvalidTimeZoneException) { throw new SnookException(SnookErrorCode.ValidationFailed, "Choose a valid habit time zone."); }
    }

    public static DateOnly Today(DateTimeOffset nowUtc, string timeZone)
        => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(nowUtc, ResolveZone(timeZone)).DateTime);

    public static HabitUpdate ValidateText(string name, string? description)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200 || description?.Length > 2000)
            throw new SnookException(SnookErrorCode.ValidationFailed, "Enter a habit name of 1–200 characters and a description of at most 2000 characters.");
        return new HabitUpdate(name.Trim(), description?.Trim() ?? string.Empty);
    }

    public static void ValidateDate(DateOnly date, DateOnly startDate, DateOnly today)
    {
        if (date < startDate || date > today || date.DayNumber < today.DayNumber - MaximumDays + 1)
            throw new SnookException(SnookErrorCode.ValidationFailed,
                "Choose a day on or after the habit's start, within the last 366 days, and no later than today in its time zone.");
    }
}
