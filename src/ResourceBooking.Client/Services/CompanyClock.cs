using ResourceBooking.Shared;

namespace ResourceBooking.Client.Services;

/// <summary>"Now" and "today" in the company's zone (London), whatever zone the browser is in.</summary>
public sealed class CompanyClock(TimeProvider clock)
{
    // The server reads Booking:TimeZone with the same default; the deployment doesn't override it.
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById(ApiCodes.CompanyZone);

    public DateTime Now => DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), Zone).DateTime, DateTimeKind.Unspecified);

    public DateOnly Today => DateOnly.FromDateTime(Now);
}
