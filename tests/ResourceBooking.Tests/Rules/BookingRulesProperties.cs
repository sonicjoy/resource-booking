using CsCheck;
using ResourceBooking.Bookings;
using ResourceBooking.Data.Entities;
using ResourceBooking.Shared;

namespace ResourceBooking.Tests.Rules;

/// <summary>Properties that must hold for any request, checked over generated inputs.</summary>
public class BookingRulesProperties
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    private static readonly DateTimeOffset Origin = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // Any instant in 2026 to 2027 on a 15-minute grid, so DST changes in both directions are covered.
    private static readonly Gen<DateTimeOffset> Instant = Gen.Int[0, 2 * 365 * 96].Select(q => Origin.AddMinutes(15 * q));
    private static readonly Gen<TimeSpan> Length = Gen.Int[1, 6 * 96].Select(q => TimeSpan.FromMinutes(15 * q));

    private static readonly Gen<Resource> AnyResource =
        Gen.Select(Gen.Enum<ResourceType>(), Gen.Int[0, 23], Gen.Int[1, 24], Gen.Int[0, 30], Gen.Bool, Gen.Int[0, 5])
            .Select((type, open, span, cap, hasHours, lim) => new Resource
            {
                Name = "R",
                Type = type,
                Capacity = cap,
                Units = Math.Max(1, lim),
                OpensAt = hasHours ? new TimeOnly(open, 0) : null,
                ClosesAt = hasHours ? new TimeOnly(Math.Min(23, open + span), 59) : null,
                MinDuration = lim == 0 ? null : TimeSpan.FromMinutes(15 * lim),
                MaxDuration = lim == 0 ? null : TimeSpan.FromHours(lim),
                MinNotice = lim == 0 ? null : TimeSpan.FromHours(lim),
                MaxDaysAhead = lim == 0 ? null : 30 * lim,
                MaxFuturePerUser = lim == 0 ? null : lim,
                LastDay = lim == 0 ? null : new DateOnly(2027, lim, 1),
            });

    [Fact]
    public void Check_never_throws() =>
        Gen.Select(AnyResource, Instant, Length, Gen.Int[-5, 40], Instant, Gen.Int[0, 6], Gen.Int[-1, 5])
            .Sample((resource, start, length, attendees, now, future, unit) =>
            {
                var candidate = new BookingCandidate(start, start + length, attendees, start + length / 2);
                _ = BookingRules.Check(candidate, resource, new RuleContext(future, unit < 0 ? null : unit), now, London);
            });

    [Fact]
    public void An_accepted_room_booking_lies_inside_opening_hours_on_one_local_day()
    {
        var room = new Resource
        {
            Name = "Thames",
            Type = ResourceType.MeetingRoom,
            Capacity = 8,
            OpensAt = new TimeOnly(8, 0),
            ClosesAt = new TimeOnly(18, 0),
        };

        Gen.Select(Instant, Length).Sample((start, length) =>
        {
            var end = start + length;
            if (BookingRules.Check(new BookingCandidate(start, end, 1), room, new RuleContext(), Origin, London).Count > 0)
            {
                return;
            }
            var localStart = TimeZoneInfo.ConvertTime(start, London);
            var localEnd = TimeZoneInfo.ConvertTime(end, London);
            Assert.Equal(localStart.Date, localEnd.Date);
            Assert.True(TimeOnly.FromDateTime(localStart.DateTime) >= room.OpensAt);
            Assert.True(TimeOnly.FromDateTime(localEnd.DateTime) <= room.ClosesAt);
        });
    }

    [Fact]
    public void An_accepted_booking_ends_after_it_starts_and_within_its_maximum_length() =>
        Gen.Select(AnyResource, Instant, Gen.Int[-8, 6 * 96]).Sample((resource, start, quarters) =>
        {
            if (resource.Type == ResourceType.Catering)
            {
                return;
            }
            var end = start.AddMinutes(15 * quarters);
            var candidate = new BookingCandidate(start, end, 1);
            if (BookingRules.Check(candidate, resource, new RuleContext(), Origin, London).Count > 0)
            {
                return;
            }
            Assert.True(end > start);
            if (resource.MaxDuration is { } max)
            {
                Assert.True(end - start <= max);
            }
        });

    [Fact]
    public void An_accepted_booking_never_exceeds_capacity() =>
        Gen.Select(AnyResource, Instant, Gen.Int[1, 40]).Sample((resource, start, attendees) =>
        {
            var candidate = new BookingCandidate(start, start.AddHours(1), attendees, start);
            if (BookingRules.Check(candidate, resource, new RuleContext(), Origin, London).Count == 0)
            {
                Assert.True(attendees <= resource.Capacity);
            }
        });
}
