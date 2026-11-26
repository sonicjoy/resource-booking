namespace ResourceBooking.Client.Services;

/// <summary>"Now" and "today" in the company's zone (London), whatever zone the browser is in.</summary>
public sealed class CompanyClock(TimeProvider clock)
{
    private static readonly TimeZoneInfo Zone = FindZone();

    public DateTime Now => DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), Zone).DateTime, DateTimeKind.Unspecified);

    public DateOnly Today => DateOnly.FromDateTime(Now);

    private static TimeZoneInfo FindZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Local;
        }
    }
}
