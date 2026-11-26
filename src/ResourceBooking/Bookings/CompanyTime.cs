namespace ResourceBooking.Bookings;

/// <summary>Converts between the wire's company-local times and stored UTC instants.</summary>
public static class CompanyTime
{
    /// <summary>
    /// A company-local wall-clock time as UTC, or null when it can't be one: it carries an offset
    /// (the API takes local times only), the clock change skips it or passes it twice, it is finer than the
    /// database's microseconds, or it is too near the end of the calendar for a day or a gap to be added.
    /// </summary>
    public static DateTimeOffset? ToUtc(DateTime local, TimeZoneInfo zone)
    {
        if (local.Kind != DateTimeKind.Unspecified || local.Ticks % 10 != 0 || !InRange(DateOnly.FromDateTime(local))
            || zone.IsInvalidTime(local) || zone.IsAmbiguousTime(local))
        {
            return null;
        }
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone));
    }

    /// <summary>False for the calendar's last year, where adding a day or a turnaround gap would overflow.</summary>
    public static bool InRange(DateOnly day) => day.Year < 9999;

    public static DateTime ToLocal(DateTimeOffset instant, TimeZoneInfo zone) =>
        DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(instant, zone).DateTime, DateTimeKind.Unspecified);

    public static DateOnly LocalDate(DateTimeOffset instant, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);

    /// <summary>The UTC instant a local day begins; where midnight is skipped by a clock change, the first valid moment.</summary>
    public static DateTimeOffset StartOfDay(DateOnly day, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue);
        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(30);
        }
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone));
    }
}
