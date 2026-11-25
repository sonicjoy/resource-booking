using ResourceBooking.Bookings;
using ResourceBooking.Data.Entities;
using ResourceBooking.Shared;

namespace ResourceBooking.Tests.Rules;

/// <summary>
/// One test per refusal code, checking the code and the message the user sees (Goal 2),
/// plus the boundaries the design names.
/// </summary>
public class BookingRulesTests
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    // Wednesday 25 November 2026, 09:00 in London (GMT, so local time equals UTC).
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 9, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(int dayOffset, int hour, int minute = 0) =>
        new DateTimeOffset(2026, 11, 25, 0, 0, 0, TimeSpan.Zero).AddDays(dayOffset).AddHours(hour).AddMinutes(minute);

    private static Resource Thames() => new()
    {
        Name = "Thames",
        Type = ResourceType.MeetingRoom,
        Capacity = 8,
        OpensAt = new TimeOnly(8, 0),
        ClosesAt = new TimeOnly(18, 0),
        MaxDuration = TimeSpan.FromHours(4),
    };

    private static Resource Car() => new()
    {
        Name = "Pool Car 1",
        Type = ResourceType.Car,
        Capacity = 4,
        OpensAt = new TimeOnly(7, 0),
        ClosesAt = new TimeOnly(19, 0),
        MaxDuration = TimeSpan.FromDays(3),
        Turnaround = TimeSpan.FromMinutes(60),
    };

    private static Resource Kitchen() => new()
    {
        Name = "Main Kitchen",
        Type = ResourceType.Catering,
        Capacity = 25,
        Units = 5,
        OpensAt = new TimeOnly(8, 0),
        ClosesAt = new TimeOnly(16, 0),
    };

    private static BookingCandidate Room(DateTimeOffset start, DateTimeOffset end, int attendees = 6) => new(start, end, attendees);

    private static BookingCandidate Order(DateTimeOffset delivery, int portions = 8)
    {
        var day = new DateTimeOffset(delivery.Date, TimeSpan.Zero);
        return new BookingCandidate(day, day.AddDays(1), portions, delivery);
    }

    private static IReadOnlyList<RuleViolation> Check(BookingCandidate candidate, Resource resource, RuleContext? context = null) =>
        BookingRules.Check(candidate, resource, context ?? new RuleContext(), Now, London);

    private static void AssertOnly(IReadOnlyList<RuleViolation> violations, string code, string message) =>
        Assert.Equal([new RuleViolation(code, message)], violations);

    [Fact]
    public void A_booking_inside_every_rule_passes() =>
        Assert.Empty(Check(Room(At(1, 14), At(1, 15)), Thames()));

    [Fact]
    public void In_past() =>
        AssertOnly(Check(Room(At(-1, 10), At(-1, 11)), Thames()), RuleCodes.InPast, "That time has already passed");

    [Fact]
    public void Too_soon()
    {
        var room = Thames();
        room.MinNotice = TimeSpan.FromHours(2);

        AssertOnly(Check(Room(At(0, 10), At(0, 11)), room), RuleCodes.TooSoon, "Thames needs 2 hours' notice");
    }

    [Fact]
    public void Too_far_ahead()
    {
        var room = Thames();
        room.MaxDaysAhead = 30;

        AssertOnly(Check(Room(At(31, 10), At(31, 11)), room), RuleCodes.TooFarAhead, "Thames can be booked up to 30 days ahead");
        Assert.Empty(Check(Room(At(30, 10), At(30, 11)), room));
    }

    [Fact]
    public void Room_ending_after_closing_time() =>
        AssertOnly(Check(Room(At(1, 15), At(1, 18, 30)), Thames()), RuleCodes.OutsideOpeningHours, "Thames closes at 18:00");

    [Fact]
    public void Room_starting_before_opening_time() =>
        AssertOnly(Check(Room(At(1, 7, 30), At(1, 9)), Thames()), RuleCodes.OutsideOpeningHours, "Thames opens at 08:00");

    [Fact]
    public void Opening_and_closing_times_are_inclusive() =>
        Assert.Empty(Check(Room(At(1, 14), At(1, 18)), Thames()));

    [Fact]
    public void A_room_booking_stays_within_one_day_even_without_opening_hours()
    {
        var room = new Resource { Name = "Avon", Type = ResourceType.MeetingRoom, Capacity = 4 };

        AssertOnly(Check(Room(At(1, 22), At(2, 1), 2), room), RuleCodes.OutsideOpeningHours, "Avon bookings must start and end on the same day");
        Assert.Empty(Check(Room(At(1, 22), At(2, 0), 2), room));
    }

    [Fact]
    public void Car_picked_up_before_opening_time() =>
        AssertOnly(Check(Room(At(1, 6), At(1, 9), 2), Car()), RuleCodes.OutsideOpeningHours,
            "Pool Car 1 can be picked up and returned between 07:00 and 19:00");

    [Fact]
    public void Car_returned_after_closing_time() =>
        AssertOnly(Check(Room(At(1, 9), At(2, 20), 2), Car()), RuleCodes.OutsideOpeningHours,
            "Pool Car 1 can be picked up and returned between 07:00 and 19:00");

    [Fact]
    public void A_car_booking_may_span_days() =>
        Assert.Empty(Check(Room(At(1, 9), At(3, 17), 2), Car()));

    [Fact]
    public void Catering_delivered_outside_kitchen_hours() =>
        AssertOnly(Check(Order(At(1, 17)), Kitchen()), RuleCodes.OutsideOpeningHours, "Main Kitchen delivers between 08:00 and 16:00");

    [Fact]
    public void Catering_time_rules_check_the_delivery_time_not_the_day_it_holds() =>
        Assert.Empty(Check(Order(At(0, 12, 30)), Kitchen()));

    [Fact]
    public void Too_short()
    {
        var room = Thames();
        room.MinDuration = TimeSpan.FromMinutes(30);

        AssertOnly(Check(Room(At(1, 10), At(1, 10, 15)), room), RuleCodes.TooShort, "Thames bookings must last at least 30 minutes");
    }

    [Fact]
    public void Too_long_in_hours() =>
        AssertOnly(Check(Room(At(1, 9), At(1, 14)), Thames()), RuleCodes.TooLong, "Thames can be booked for 4 hours at most");

    [Fact]
    public void Too_long_in_days() =>
        AssertOnly(Check(Room(At(1, 9), At(5, 9), 2), Car()), RuleCodes.TooLong, "Pool Car 1 can be booked for 3 days at most");

    [Fact]
    public void Room_over_capacity() =>
        AssertOnly(Check(Room(At(1, 10), At(1, 11), attendees: 12), Thames()), RuleCodes.OverCapacity, "Thames seats 8");

    [Fact]
    public void Catering_order_over_the_portion_limit() =>
        AssertOnly(Check(Order(At(1, 12), portions: 30), Kitchen()), RuleCodes.OverCapacity, "Main Kitchen serves up to 25 portions an order");

    [Fact]
    public void Resource_retired()
    {
        var room = Thames();
        room.LastDay = new DateOnly(2026, 11, 27);

        AssertOnly(Check(Room(At(3, 10), At(3, 11)), room), RuleCodes.ResourceRetired, "Thames can't be booked after 27 Nov 2026");
        Assert.Empty(Check(Room(At(2, 10), At(2, 11)), room));
    }

    [Fact]
    public void User_limit()
    {
        var room = Thames();
        room.MaxFuturePerUser = 2;

        AssertOnly(Check(Room(At(1, 10), At(1, 11)), room, new RuleContext(UserFutureCount: 2)),
            RuleCodes.UserLimit, "You can hold up to 2 future bookings of Thames");
        Assert.Empty(Check(Room(At(1, 10), At(1, 11)), room, new RuleContext(UserFutureCount: 1)));
    }

    [Fact]
    public void Kitchen_full() =>
        AssertOnly(Check(Order(At(1, 12)), Kitchen(), new RuleContext(FreeUnit: null)), RuleCodes.KitchenFull, "Main Kitchen takes 5 orders a day");

    [Fact]
    public void Every_broken_rule_is_reported()
    {
        var codes = Check(Room(At(1, 15), At(1, 20), attendees: 12), Thames()).Select(v => v.Code);

        Assert.Equal([RuleCodes.OutsideOpeningHours, RuleCodes.OverCapacity, RuleCodes.TooLong], codes.Order());
    }

    [Fact]
    public void Settings_that_do_not_apply_to_the_type_are_ignored()
    {
        var kitchen = Kitchen();
        kitchen.MaxDuration = TimeSpan.FromMinutes(5);
        kitchen.MinDuration = TimeSpan.FromDays(2);

        Assert.Empty(Check(Order(At(1, 12)), kitchen));
    }

    [Fact]
    public void Opening_hours_are_local_time_in_summer()
    {
        // 9 July 2026 is BST (UTC+1): 08:30-09:30 London is 07:30-08:30 UTC.
        var summerNow = new DateTimeOffset(2026, 7, 8, 9, 0, 0, TimeSpan.Zero);
        var start = new DateTimeOffset(2026, 7, 9, 7, 30, 0, TimeSpan.Zero);

        Assert.Empty(BookingRules.Check(Room(start, start.AddHours(1)), Thames(), new RuleContext(), summerNow, London));
    }

    [Theory]
    [InlineData(ResourceType.MeetingRoom, new[] { RuleField.OpeningHours, RuleField.MinDuration, RuleField.MaxDuration, RuleField.MinNotice, RuleField.MaxDaysAhead, RuleField.MaxFuturePerUser })]
    [InlineData(ResourceType.Car, new[] { RuleField.OpeningHours, RuleField.MinDuration, RuleField.MaxDuration, RuleField.MinNotice, RuleField.MaxDaysAhead, RuleField.Turnaround, RuleField.MaxFuturePerUser })]
    [InlineData(ResourceType.Catering, new[] { RuleField.OpeningHours, RuleField.MinNotice, RuleField.MaxDaysAhead, RuleField.MaxFuturePerUser, RuleField.Units })]
    public void Rule_fields_by_type(ResourceType type, RuleField[] expected) =>
        Assert.Equal(expected.Order(), RuleFields.For(type).Order());
}
