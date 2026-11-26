using Microsoft.EntityFrameworkCore;
using ResourceBooking.Data;
using ResourceBooking.Data.Entities;
using ResourceBooking.Shared;
using ResourceBooking.Shared.Dtos;

namespace ResourceBooking.Bookings;

/// <summary>Free/busy reads for the overview, "Find alternatives" and the assistant. Takes no locks.</summary>
public sealed class AvailabilityService(AppDb db, TimeZoneInfo zone)
{
    public async Task<AvailabilityDay> DayAsync(DateOnly date, ResourceType type, Guid callerId, bool isAdmin, CancellationToken ct)
    {
        var dayStart = CompanyTime.StartOfDay(date, zone);
        var dayEnd = CompanyTime.StartOfDay(date.AddDays(1), zone);
        var resources = await db.Resources.AsNoTracking()
            .Where(r => r.Type == type && (r.LastDay == null || r.LastDay >= date))
            .OrderBy(r => r.Name)
            .ToListAsync(ct);
        var ids = resources.Select(r => r.Id).ToList();
        var bookings = await db.Bookings.AsNoTracking().Include(b => b.User)
            .Where(b => ids.Contains(b.ResourceId) && b.Status == BookingStatus.Confirmed && b.StartsAt < dayEnd && b.EndsAt > dayStart)
            .OrderBy(b => b.StartsAt)
            .ToListAsync(ct);

        return new AvailabilityDay(date, resources.Select(resource =>
        {
            var own = bookings.Where(b => b.ResourceId == resource.Id).ToList();
            if (resource.Type == ResourceType.Catering)
            {
                // A kitchen order's range is its whole local day, so the day's orders start at dayStart.
                var orders = own.Count(b => b.StartsAt == dayStart);
                return new ResourceAvailability(resource.Id, resource.Name, resource.Type, resource.Capacity,
                    resource.OpensAt, resource.ClosesAt, resource.Units, Math.Max(0, resource.Units - orders), []);
            }
            var busy = own.Select(b =>
            {
                var mine = b.UserId == callerId;
                return new BusyBlock(CompanyTime.ToLocal(b.StartsAt, zone), CompanyTime.ToLocal(b.EndsAt, zone), mine,
                    mine || isAdmin ? b.Title : null, isAdmin ? b.User!.DisplayName : null);
            }).ToList();
            return new ResourceAvailability(resource.Id, resource.Name, resource.Type, resource.Capacity,
                resource.OpensAt, resource.ClosesAt, resource.Units, own.Count == 0 ? resource.Units : 0, busy);
        }).ToList());
    }

    /// <summary>
    /// Resources of a type with room for the attendees and nothing booked across the window, counting each
    /// side's turnaround gap the way the database constraint does. A kitchen is free while a unit is.
    /// </summary>
    public async Task<List<Resource>> FreeAsync(ResourceType type, DateTimeOffset start, DateTimeOffset end, int attendees, CancellationToken ct)
    {
        var day = CompanyTime.LocalDate(start, zone);
        var candidates = await db.Resources.AsNoTracking()
            .Where(r => r.Type == type && r.Capacity >= attendees && (r.LastDay == null || r.LastDay >= day))
            .OrderBy(r => r.Name)
            .ToListAsync(ct);

        var free = new List<Resource>();
        foreach (var resource in candidates)
        {
            if (type == ResourceType.Catering)
            {
                var dayStart = CompanyTime.StartOfDay(day, zone);
                var orders = await db.Bookings.CountAsync(b => b.ResourceId == resource.Id && b.Status == BookingStatus.Confirmed && b.StartsAt == dayStart, ct);
                if (orders < resource.Units)
                {
                    free.Add(resource);
                }
                continue;
            }
            var blockedUntil = end + BookingService.Turnaround(resource);
            var clash = await db.Bookings.AnyAsync(b => b.ResourceId == resource.Id && b.Status == BookingStatus.Confirmed
                && b.StartsAt < blockedUntil && start < b.BlockedUntil, ct);
            if (!clash)
            {
                free.Add(resource);
            }
        }
        return free;
    }
}
