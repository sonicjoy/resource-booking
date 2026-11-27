using Microsoft.EntityFrameworkCore;
using ResourceBooking.Bookings;
using ResourceBooking.Data;
using ResourceBooking.Data.Entities;
using ResourceBooking.Shared;
using ResourceBooking.Shared.Dtos;

namespace ResourceBooking.Reports;

/// <summary>
/// Booked time over open time per resource for local dates from..to, both included (design: Utilisation).
/// A day's window is opens_at to closes_at, or the whole day when either is unset; days after a resource's
/// last day don't count. Bookings are clipped to each window, so a ratio never passes 100%. Kitchens count
/// orders (one unit for one day each) over units times days.
/// </summary>
public sealed class Utilisation(AppDb db, TimeZoneInfo zone)
{
    public const int MaxDays = 366;

    public async Task<List<UtilisationRow>> ForAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var (resources, days) = await ByDayAsync(from, to, null, ct);
        var byResource = days.ToLookup(d => d.ResourceId);
        // Every resource gets a row, including one retired before the range (nothing open, so no ratio).
        return resources.Select(r => Row(r, byResource[r.Id].Sum(d => d.Used), byResource[r.Id].Sum(d => d.Available),
            r.Type == ResourceType.Catering ? "unit-days" : "hours")).ToList();
    }

    /// <summary>
    /// The resources (of one type, or all) and a used/available row for each resource-day from..to, both
    /// included, up to each resource's last day: hours for rooms and cars, orders for kitchens. One query for
    /// resources and one for bookings (design: month-view §2).
    /// </summary>
    public async Task<(IReadOnlyList<Resource> Resources, IReadOnlyList<ResourceDay> Days)> ByDayAsync(
        DateOnly from, DateOnly to, ResourceType? type, CancellationToken ct)
    {
        var start = CompanyTime.StartOfDay(from, zone);
        var end = CompanyTime.StartOfDay(to.AddDays(1), zone);
        var resources = await db.Resources.AsNoTracking()
            .Where(r => type == null || r.Type == type)
            .OrderBy(r => r.Type).ThenBy(r => r.Name).ToListAsync(ct);
        var ids = resources.Select(r => r.Id).ToList();
        var bookings = await db.Bookings.AsNoTracking()
            .Where(b => ids.Contains(b.ResourceId) && b.Status == BookingStatus.Confirmed && b.StartsAt < end && b.EndsAt > start)
            .Select(b => new { b.ResourceId, b.StartsAt, b.EndsAt })
            .ToListAsync(ct);
        var byResource = bookings.ToLookup(b => b.ResourceId, b => (b.StartsAt, b.EndsAt));

        var days = new List<ResourceDay>();
        foreach (var resource in resources)
        {
            var own = byResource[resource.Id].ToList();
            days.AddRange(Days(from, to).Where(d => resource.LastDay is not { } last || d <= last)
                .Select(d => DayOf(resource, d, own, zone)));
        }
        return (resources, days);
    }

    /// <summary>
    /// One resource-day. Rooms and cars: hours booked inside the day's opening window (the whole local day when
    /// either time is unset) over the window's hours. Kitchens: that day's orders, capped at the units, over the units.
    /// <paramref name="bookings"/> are the resource's confirmed bookings.
    /// </summary>
    public static ResourceDay DayOf(Resource resource, DateOnly day, IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> bookings, TimeZoneInfo zone)
    {
        if (resource.Type == ResourceType.Catering)
        {
            // A kitchen order's range is its whole local day; units may have been lowered after a busy day.
            var orders = bookings.Count(b => CompanyTime.LocalDate(b.Start, zone) == day);
            return new ResourceDay(resource.Id, day, Math.Min(orders, resource.Units), resource.Units);
        }
        var (windowStart, windowEnd) = Window(resource, day, zone);
        var used = bookings.Sum(b => Math.Max(0, (Min(b.End, windowEnd) - Max(b.Start, windowStart)).TotalHours));
        return new ResourceDay(resource.Id, day, used, (windowEnd - windowStart).TotalHours);
    }

    private static (DateTimeOffset Start, DateTimeOffset End) Window(Resource resource, DateOnly day, TimeZoneInfo zone) =>
        resource.OpensAt is { } opens && resource.ClosesAt is { } closes
            ? (CompanyTime.At(day, opens, zone), CompanyTime.At(day, closes, zone))
            : (CompanyTime.StartOfDay(day, zone), CompanyTime.StartOfDay(day.AddDays(1), zone));

    private static UtilisationRow Row(Resource r, double used, double available, string unit) =>
        new(r.Id, r.Name, r.Type, r.LastDay, used, available, unit, available > 0 ? used / available : null);

    private static IEnumerable<DateOnly> Days(DateOnly from, DateOnly to)
    {
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            yield return d;
        }
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
}

public sealed record ResourceDay(Guid ResourceId, DateOnly Day, double Used, double Available);
