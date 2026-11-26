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
        var start = CompanyTime.StartOfDay(from, zone);
        var end = CompanyTime.StartOfDay(to.AddDays(1), zone);
        var resources = await db.Resources.AsNoTracking().OrderBy(r => r.Type).ThenBy(r => r.Name).ToListAsync(ct);
        var bookings = await db.Bookings.AsNoTracking()
            .Where(b => b.Status == BookingStatus.Confirmed && b.StartsAt < end && b.EndsAt > start)
            .Select(b => new { b.ResourceId, b.StartsAt, b.EndsAt })
            .ToListAsync(ct);
        var byResource = bookings.ToLookup(b => b.ResourceId, b => (b.StartsAt, b.EndsAt));

        return resources.Select(resource =>
        {
            var days = Days(from, to).Where(d => resource.LastDay is not { } last || d <= last).ToList();
            var own = byResource[resource.Id].ToList();
            if (resource.Type == ResourceType.Catering)
            {
                // Orders per local day, capped at the kitchen's units in case they were lowered after a busy day.
                var counted = days.ToHashSet();
                double orders = own.GroupBy(b => CompanyTime.LocalDate(b.StartsAt, zone))
                    .Where(g => counted.Contains(g.Key))
                    .Sum(g => Math.Min(g.Count(), resource.Units));
                double unitDays = resource.Units * days.Count;
                return Row(resource, orders, unitDays, "unit-days");
            }
            double open = 0, used = 0;
            foreach (var (windowStart, windowEnd) in days.Select(d => Window(resource, d)))
            {
                open += (windowEnd - windowStart).TotalHours;
                used += own.Sum(b => Math.Max(0, (Min(b.EndsAt, windowEnd) - Max(b.StartsAt, windowStart)).TotalHours));
            }
            return Row(resource, used, open, "hours");
        }).ToList();
    }

    private (DateTimeOffset Start, DateTimeOffset End) Window(Resource resource, DateOnly day) =>
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
