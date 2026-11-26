using Microsoft.EntityFrameworkCore;
using ResourceBooking.Auth;
using ResourceBooking.Bookings;
using ResourceBooking.Data;
using ResourceBooking.Data.Entities;
using ResourceBooking.Shared;
using ResourceBooking.Shared.Dtos;

namespace ResourceBooking.Endpoints;

/// <summary>What can be booked, and when: the overview, "Find alternatives", and the resource list.</summary>
public static class AvailabilityEndpoints
{
    public static RouteGroupBuilder MapAvailabilityEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/availability", async (DateOnly date, ResourceType type, HttpContext http, AvailabilityService availability, CancellationToken ct) =>
            CompanyTime.InRange(date)
                ? Results.Ok(await availability.DayAsync(date, type, http.User.UserId()!.Value, http.User.IsInRole(nameof(Role.Admin)), ct))
                : ApiResults.Problem(ApiError.BadRequest("Give a date before the year 9999")));

        api.MapGet("/resources/free", async (ResourceType type, DateTime start, DateTime end, int attendees,
            AvailabilityService availability, TimeZoneInfo zone, CancellationToken ct) =>
        {
            if (CompanyTime.ToUtc(start, zone) is not { } from || CompanyTime.ToUtc(end, zone) is not { } until || until <= from)
            {
                return ApiResults.Problem(ApiError.BadRequest("Give a local start before a local end"));
            }
            var free = await availability.FreeAsync(type, from, until, Math.Max(1, attendees), ct);
            return Results.Ok(free.Select(ToDto).ToList());
        });

        api.MapGet("/resources", async (HttpContext http, AppDb db, TimeProvider clock, TimeZoneInfo zone, CancellationToken ct) =>
        {
            var today = CompanyTime.LocalDate(clock.GetUtcNow(), zone);
            var all = http.User.IsInRole(nameof(Role.Admin));
            var resources = await db.Resources.AsNoTracking()
                .Where(r => all || r.LastDay == null || r.LastDay >= today)
                .OrderBy(r => r.Name)
                .ToListAsync(ct);
            return resources.Select(ToDto).ToList();
        });

        // Employees see the same resources by id as in the list: retired ones are admin-only.
        api.MapGet("/resources/{id:guid}", async (Guid id, HttpContext http, AppDb db, TimeProvider clock, TimeZoneInfo zone, CancellationToken ct) =>
        {
            var today = CompanyTime.LocalDate(clock.GetUtcNow(), zone);
            var all = http.User.IsInRole(nameof(Role.Admin));
            return await db.Resources.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id && (all || r.LastDay == null || r.LastDay >= today), ct) is { } resource
                ? Results.Ok(ToDto(resource))
                : ApiResults.Problem(ApiError.NotFound("No such resource"));
        });

        return api;
    }

    public static ResourceDto ToDto(Resource r) => new(
        r.Id, r.Name, r.Type, r.Capacity, r.Units, r.LastDay, r.OpensAt, r.ClosesAt,
        r.MinDuration, r.MaxDuration, r.MinNotice, r.MaxDaysAhead, r.Turnaround, r.MaxFuturePerUser);
}
