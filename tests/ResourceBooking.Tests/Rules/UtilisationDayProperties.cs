using CsCheck;
using ResourceBooking.Bookings;
using ResourceBooking.Data.Entities;
using ResourceBooking.Reports;
using ResourceBooking.Shared;

namespace ResourceBooking.Tests.Rules;

/// <summary>
/// The per-resource, per-day calculation behind the month view and the Utilisation report (design: month-view §2),
/// checked over generated resources and non-overlapping bookings around both 2026 DST changes.
/// </summary>
public class UtilisationDayProperties
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    // A first day within a week of either DST change in 2026 (29 March, 25 October), or an ordinary day.
    private static readonly Gen<DateOnly> FirstDay = Gen.OneOf(
        Gen.Int[-3, 1].Select(d => new DateOnly(2026, 3, 29).AddDays(d)),
        Gen.Int[-3, 1].Select(d => new DateOnly(2026, 10, 25).AddDays(d)),
        Gen.Int[0, 364].Select(d => new DateOnly(2026, 1, 1).AddDays(d)));

    private static readonly Gen<Resource> RoomOrCar =
        Gen.Select(Gen.Bool, Gen.Int[0, 3], Gen.Int[6, 10], Gen.Int[16, 20]).Select((car, hours, opens, closes) => new Resource
        {
            Name = "R",
            Type = car ? ResourceType.Car : ResourceType.MeetingRoom,
            Capacity = 4,
            // 0: both hours set; 1: none; 2: only opening; 3: only closing.
            OpensAt = hours is 0 or 2 ? new TimeOnly(opens, 0) : null,
            ClosesAt = hours is 0 or 3 ? new TimeOnly(closes, 0) : null,
        });

    // Bookings laid end to end with gaps, so they never overlap, as the database guarantees.
    private static Gen<List<(DateTimeOffset Start, DateTimeOffset End)>> Bookings(DateOnly first) =>
        Gen.Select(Gen.Int[0, 48], Gen.Int[1, 40]).List[0, 6].Select(parts =>
        {
            var at = CompanyTime.StartOfDay(first, London);
            var list = new List<(DateTimeOffset, DateTimeOffset)>();
            foreach (var (gap, length) in parts)
            {
                var start = at.AddMinutes(15 * gap);
                at = start.AddMinutes(15 * length);
                list.Add((start, at));
            }
            return list;
        });

    private static IEnumerable<DateOnly> Days(DateOnly first, int count) => Enumerable.Range(0, count).Select(first.AddDays);

    [Fact]
    public void Used_is_never_negative_and_never_more_than_available()
    {
        Gen.Select(RoomOrCar, FirstDay).SelectMany((r, first) => Bookings(first).Select(b => (r, first, b))).Sample(t =>
        {
            foreach (var day in Days(t.first, 4))
            {
                var d = Utilisation.DayOf(t.r, day, t.b, London);
                Assert.InRange(d.Used, 0, d.Available);
            }
        }, iter: 2_000);
    }

    [Fact]
    public void Without_opening_hours_the_days_add_up_to_every_booked_hour()
    {
        var allDay = new Resource { Name = "Shed", Type = ResourceType.MeetingRoom, Capacity = 2 };
        FirstDay.SelectMany(first => Bookings(first).Select(b => (first, b))).Sample(t =>
        {
            // Bookings start on the first day and last at most a few days; six days cover them all.
            var used = Days(t.first, 6).Sum(day => Utilisation.DayOf(allDay, day, t.b, London).Used);
            Assert.Equal(t.b.Sum(x => (x.End - x.Start).TotalHours), used, 6);
        }, iter: 2_000);
    }

    [Fact]
    public void Without_opening_hours_a_day_is_as_long_as_the_clock_says()
    {
        var allDay = new Resource { Name = "Shed", Type = ResourceType.MeetingRoom, Capacity = 2 };
        FirstDay.Sample(day =>
        {
            var expected = (CompanyTime.StartOfDay(day.AddDays(1), London) - CompanyTime.StartOfDay(day, London)).TotalHours;
            Assert.Equal(expected, Utilisation.DayOf(allDay, day, [], London).Available);
        });
        Assert.Equal(8760, Days(new DateOnly(2026, 1, 1), 365).Sum(d => Utilisation.DayOf(allDay, d, [], London).Available));
    }

    [Theory]
    [InlineData(2026, 3, 29, 23)]
    [InlineData(2026, 10, 25, 25)]
    [InlineData(2026, 11, 26, 24)]
    public void A_day_without_opening_hours_has_23_24_or_25_hours(int y, int m, int d, double hours) =>
        Assert.Equal(hours, Utilisation.DayOf(new Resource { Name = "Shed", Capacity = 1 }, new DateOnly(y, m, d), [], London).Available);

    [Fact]
    public void A_midnight_crossing_booking_counts_on_both_days()
    {
        var shed = new Resource { Name = "Shed", Type = ResourceType.Car, Capacity = 2 };
        (DateTimeOffset, DateTimeOffset)[] trip = [(At(20, 22), At(21, 4))];

        Assert.Equal((24.0, 2.0), Pair(Utilisation.DayOf(shed, new DateOnly(2026, 11, 20), trip, London)));
        Assert.Equal((24.0, 4.0), Pair(Utilisation.DayOf(shed, new DateOnly(2026, 11, 21), trip, London)));
    }

    [Fact]
    public void Booked_hours_are_clipped_to_the_opening_window()
    {
        var car = new Resource { Name = "Car", Type = ResourceType.Car, Capacity = 4, OpensAt = new(7, 0), ClosesAt = new(19, 0) };

        var day = Utilisation.DayOf(car, new DateOnly(2026, 11, 20), [(At(20, 6), At(20, 9)), (At(20, 18), At(20, 22))], London);

        Assert.Equal((12.0, 2 + 1.0), Pair(day));
    }

    [Fact]
    public void A_window_that_opens_and_closes_at_the_same_time_has_no_hours()
    {
        var room = new Resource { Name = "Box", Type = ResourceType.MeetingRoom, Capacity = 4, OpensAt = new(9, 0), ClosesAt = new(9, 0) };

        Assert.Equal((0.0, 0.0), Pair(Utilisation.DayOf(room, new DateOnly(2026, 11, 20), [(At(20, 8), At(20, 10))], London)));
    }

    [Fact]
    public void A_kitchen_counts_that_days_orders_capped_at_its_units()
    {
        var kitchen = new Resource { Name = "Kitchen", Type = ResourceType.Catering, Capacity = 20, Units = 2 };
        var day = CompanyTime.StartOfDay(new DateOnly(2026, 11, 20), London);
        var next = CompanyTime.StartOfDay(new DateOnly(2026, 11, 21), London);
        (DateTimeOffset, DateTimeOffset)[] orders = [(day, next), (day, next), (day, next), (next, next.AddDays(1))];

        Assert.Equal((2.0, 2.0), Pair(Utilisation.DayOf(kitchen, new DateOnly(2026, 11, 20), orders, London)));
        Assert.Equal((2.0, 1.0), Pair(Utilisation.DayOf(kitchen, new DateOnly(2026, 11, 21), orders, London)));
    }

    private static (double Available, double Used) Pair(ResourceDay d) => (d.Available, d.Used);

    private static DateTimeOffset At(int day, int hour) => CompanyTime.ToUtc(new DateTime(2026, 11, day, hour, 0, 0), London)!.Value;
}
