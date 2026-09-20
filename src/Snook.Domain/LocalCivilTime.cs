namespace Snook.Domain;

public enum AmbiguousTimeChoice
{
    Earlier,
    Later
}

public enum ReminderDeliveryPolicy
{
    Inexact
}

public static class LocalCivilTime
{
    public static DateTimeOffset Resolve(DateOnly date, TimeOnly time,
        string timeZone, AmbiguousTimeChoice ambiguousChoice)
    {
        if (date.Year < 1900)
            throw new SnookException(SnookErrorCode.ValidationFailed,
                "Local reminder dates must be on or after 1900-01-01.");
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(timeZone); }
        catch (Exception exception) when (exception is TimeZoneNotFoundException
            or InvalidTimeZoneException or ArgumentException)
        {
            throw new SnookException(SnookErrorCode.ValidationFailed,
                "Choose a valid reminder time zone.", exception);
        }
        var local = DateTime.SpecifyKind(date.ToDateTime(time), DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
            throw new SnookException(SnookErrorCode.ValidationFailed,
                "That local reminder time does not exist because the clock moves forward. Choose another time.");
        if (!zone.IsAmbiguousTime(local))
            return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
        var candidates = zone.GetAmbiguousTimeOffsets(local)
            .Select(offset => new DateTimeOffset(local, offset).ToUniversalTime())
            .OrderBy(instant => instant)
            .ToArray();
        return ambiguousChoice == AmbiguousTimeChoice.Earlier ? candidates[0] : candidates[^1];
    }
}
