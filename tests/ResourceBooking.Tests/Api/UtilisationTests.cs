using System.Net;
using System.Net.Http.Json;
using ResourceBooking.Bookings;
using ResourceBooking.Data;
using ResourceBooking.Data.Entities;
using ResourceBooking.Shared;
using ResourceBooking.Shared.Dtos;
using ResourceBooking.Tests.Infrastructure;

namespace ResourceBooking.Tests.Api;

/// <summary>
/// Booked time over open time per resource for a range of local dates, both ends included. The seed's
/// past bookings fall in "last week": Wednesday 18 to Tuesday 24 November 2026 (London is on GMT).
/// </summary>
public class UtilisationTests(DbFixture fixture) : IAsyncLifetime
{
    private ApiFactory _app = null!;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    public async ValueTask InitializeAsync() => _app = await ApiFactory.CreateAsync(fixture);

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    private async Task<List<UtilisationRow>> ReportAsync(string from = "2026-11-18", string to = "2026-11-24")
    {
        var sam = await _app.SignedInAsync(Seed.SamId);
        return (await sam.GetFromJsonAsync<List<UtilisationRow>>($"/api/reports/utilisation?from={from}&to={to}", Ct))!;
    }

    private static DateTimeOffset At(int day, int hour) => CompanyTime.ToUtc(new DateTime(2026, 11, day, hour, 0, 0), London)!.Value;

    [Fact]
    public async Task A_multi_day_car_booking_counts_only_its_open_hours_each_day()
    {
        var car = (await ReportAsync()).Single(r => r.ResourceId == Seed.PoolCar1Id);

        // Client tour: Thu 19 08:00 to Sat 21 17:00; the car is open 07:00-19:00, 12 hours a day.
        Assert.Equal(("hours", 84.0, 11 + 12 + 10.0), (car.Unit, car.Available, car.Used));
        Assert.Equal(33.0 / 84, car.Ratio!.Value, 6);
    }

    [Fact]
    public async Task Rooms_add_up_each_booking_and_kitchens_count_unit_days()
    {
        var rows = await ReportAsync();

        var thames = rows.Single(r => r.ResourceId == Seed.ThamesId);
        var kitchen = rows.Single(r => r.ResourceId == Seed.MainKitchenId);
        Assert.Equal((70.0, 2 + 2 + 2.0), (thames.Available, thames.Used));
        Assert.Equal(("unit-days", 35.0, 1.0), (kitchen.Unit, kitchen.Available, kitchen.Used));
    }

    [Fact]
    public async Task A_resource_without_opening_hours_is_open_all_day()
    {
        await using (var db = _app.Db())
        {
            var shed = new Resource { Id = Guid.CreateVersion7(), Name = "Shed", Type = ResourceType.MeetingRoom, Capacity = 2 };
            db.Resources.Add(shed);
            db.Bookings.Add(new Booking
            {
                Id = Guid.CreateVersion7(),
                ResourceId = shed.Id,
                UserId = Seed.AnaId,
                Attendees = 1,
                Status = BookingStatus.Confirmed,
                StartsAt = At(20, 22),
                EndsAt = At(21, 4),
                BlockedUntil = At(21, 4),
            });
            await db.SaveChangesAsync(Ct);
        }

        var shedRow = (await ReportAsync()).Single(r => r.Name == "Shed");

        Assert.Equal((168.0, 6.0), (shedRow.Available, shedRow.Used));
    }

    [Fact]
    public async Task A_retired_resource_counts_only_days_up_to_its_last_day_and_still_appears()
    {
        await using (var db = _app.Db())
        {
            db.Resources.Single(r => r.Id == Seed.ThamesId).LastDay = new DateOnly(2026, 11, 20);
            await db.SaveChangesAsync(Ct);
        }

        var thames = (await ReportAsync()).Single(r => r.ResourceId == Seed.ThamesId);

        // Open 18, 19 and 20 November, 10 hours each; Design review (18th) and Sprint planning (19th) count.
        Assert.Equal((30.0, 4.0, new DateOnly(2026, 11, 20)), (thames.Available, thames.Used, thames.LastDay));
    }

    [Fact]
    public async Task Cancelled_bookings_do_not_count()
    {
        await using (var db = _app.Db())
        {
            foreach (var b in db.Bookings.Where(b => b.ResourceId == Seed.ThamesId))
            {
                b.Status = BookingStatus.Cancelled;
            }
            await db.SaveChangesAsync(Ct);
        }

        Assert.Equal(0, (await ReportAsync()).Single(r => r.ResourceId == Seed.ThamesId).Used);
    }

    [Theory]
    [InlineData("2026-11-24", "2026-11-18")]
    [InlineData("2026-01-01", "2027-06-01")]
    [InlineData("9999-12-30", "9999-12-31")]
    public async Task A_backwards_too_long_or_out_of_range_window_is_400(string from, string to)
    {
        var sam = await _app.SignedInAsync(Seed.SamId);

        var response = await sam.GetAsync($"/api/reports/utilisation?from={from}&to={to}", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_kitchen_never_uses_more_units_in_a_day_than_it_has()
    {
        await using (var db = _app.Db())
        {
            var lunch = db.Bookings.Single(b => b.ResourceId == Seed.MainKitchenId);
            foreach (var unit in new[] { 1, 2 })
            {
                db.Bookings.Add(new Booking
                {
                    Id = Guid.CreateVersion7(),
                    ResourceId = Seed.MainKitchenId,
                    UserId = Seed.BenId,
                    Attendees = 4,
                    Status = BookingStatus.Confirmed,
                    StartsAt = lunch.StartsAt,
                    EndsAt = lunch.EndsAt,
                    BlockedUntil = lunch.BlockedUntil,
                    UnitNo = unit,
                    DeliveryAt = lunch.DeliveryAt,
                    DeliveryRoomId = lunch.DeliveryRoomId,
                });
            }
            db.Resources.Single(r => r.Id == Seed.MainKitchenId).Units = 1;
            await db.SaveChangesAsync(Ct);
        }

        var kitchen = (await ReportAsync()).Single(r => r.ResourceId == Seed.MainKitchenId);

        Assert.Equal((7.0, 1.0), (kitchen.Available, kitchen.Used));
    }
}
