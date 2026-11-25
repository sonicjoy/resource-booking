using System.Globalization;
using ResourceBooking.Data.Entities;
using ResourceBooking.Shared;

namespace ResourceBooking.Bookings;

/// <summary>A booking to check, with its times already converted to UTC.</summary>
/// <param name="DeliveryAt">For a catering order: when it is delivered. The time rules check this instant.</param>
public sealed record BookingCandidate(DateTimeOffset Start, DateTimeOffset End, int Attendees, DateTimeOffset? DeliveryAt = null);

/// <summary>What the rules need from the database, loaded by the caller under its locks.</summary>
/// <param name="UserFutureCount">The user's confirmed bookings on this resource that have not ended, excluding the one being edited.</param>
/// <param name="FreeUnit">For a kitchen: the lowest free unit that day, or null if every unit is taken.</param>
public sealed record RuleContext(int UserFutureCount = 0, int? FreeUnit = 0);

public sealed record RuleViolation(string Code, string Message);

/// <summary>
/// Every booking rule except overlaps, which the database refuses. One method, no I/O:
/// adding a rule means a resource field, one check here, one code in <see cref="RuleCodes"/> and one test.
/// </summary>
public static class BookingRules
{
    /// <summary>Returns every rule the booking breaks; an empty list means it may go ahead.</summary>
    public static IReadOnlyList<RuleViolation> Check(
        BookingCandidate candidate, Resource resource, RuleContext context, DateTimeOffset now, TimeZoneInfo zone)
    {
        var applies = RuleFields.For(resource.Type);
        var isKitchen = resource.Type == ResourceType.Catering;
        var name = resource.Name;
        var violations = new List<RuleViolation>();
        void Refuse(string code, string message) => violations.Add(new RuleViolation(code, message));

        // A catering order holds its unit for the whole day; the time rules look at the delivery.
        var start = isKitchen ? candidate.DeliveryAt ?? candidate.Start : candidate.Start;
        var end = candidate.End;
        var localStart = TimeZoneInfo.ConvertTime(start, zone).DateTime;
        var localEnd = TimeZoneInfo.ConvertTime(end, zone).DateTime;
        var localToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);

        if (start < now)
        {
            Refuse(RuleCodes.InPast, "That time has already passed");
        }
        else if (applies.Contains(RuleField.MinNotice) && resource.MinNotice is { } notice && start - now < notice)
        {
            Refuse(RuleCodes.TooSoon, $"{name} needs {Describe(notice)}' notice");
        }

        if (applies.Contains(RuleField.MaxDaysAhead) && resource.MaxDaysAhead is { } daysAhead
            && DateOnly.FromDateTime(localStart) > localToday.AddDays(daysAhead))
        {
            Refuse(RuleCodes.TooFarAhead, $"{name} can be booked up to {daysAhead} days ahead");
        }

        if (OpeningHoursMessage(resource, applies.Contains(RuleField.OpeningHours), localStart, localEnd) is { } hours)
        {
            Refuse(RuleCodes.OutsideOpeningHours, hours);
        }

        var length = end - candidate.Start;
        if (applies.Contains(RuleField.MinDuration) && resource.MinDuration is { } shortest && length < shortest)
        {
            Refuse(RuleCodes.TooShort, $"{name} bookings must last at least {Describe(shortest)}");
        }
        if (applies.Contains(RuleField.MaxDuration) && resource.MaxDuration is { } longest && length > longest)
        {
            Refuse(RuleCodes.TooLong, $"{name} can be booked for {Describe(longest)} at most");
        }

        if (candidate.Attendees > resource.Capacity)
        {
            Refuse(RuleCodes.OverCapacity, isKitchen
                ? $"{name} serves up to {resource.Capacity} portions an order"
                : $"{name} seats {resource.Capacity}");
        }

        if (resource.LastDay is { } lastDay)
        {
            // Rooms and cars may not run past the end of the last day; a kitchen order is for one day.
            var lastMoment = TimeZoneInfo.ConvertTimeToUtc(lastDay.AddDays(1).ToDateTime(TimeOnly.MinValue), zone);
            var beyond = isKitchen ? DateOnly.FromDateTime(localStart) > lastDay : end > lastMoment;
            if (beyond)
            {
                Refuse(RuleCodes.ResourceRetired,
                    $"{name} can't be booked after {lastDay.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}");
            }
        }

        if (applies.Contains(RuleField.MaxFuturePerUser) && resource.MaxFuturePerUser is { } limit
            && context.UserFutureCount >= limit)
        {
            Refuse(RuleCodes.UserLimit, $"You can hold up to {limit} future bookings of {name}");
        }

        if (isKitchen && context.FreeUnit is null)
        {
            Refuse(RuleCodes.KitchenFull, $"{name} takes {resource.Units} orders a day");
        }

        return violations;
    }

    private static string? OpeningHoursMessage(Resource resource, bool hoursApply, DateTime localStart, DateTime localEnd)
    {
        var name = resource.Name;
        var opens = hoursApply ? resource.OpensAt : null;
        var closes = hoursApply ? resource.ClosesAt : null;
        var startTime = localStart.TimeOfDay;

        switch (resource.Type)
        {
            case ResourceType.MeetingRoom:
                // Ending exactly at midnight still counts as the same day.
                var endTime = localEnd.Date == localStart.Date ? localEnd.TimeOfDay
                    : localEnd.Date == localStart.Date.AddDays(1) && localEnd.TimeOfDay == TimeSpan.Zero ? TimeSpan.FromDays(1)
                    : (TimeSpan?)null;
                if (endTime is null)
                {
                    return $"{name} bookings must start and end on the same day";
                }
                if (opens is { } roomOpens && startTime < roomOpens.ToTimeSpan())
                {
                    return $"{name} opens at {roomOpens:HH\\:mm}";
                }
                if (closes is { } roomCloses && endTime > roomCloses.ToTimeSpan())
                {
                    return $"{name} closes at {roomCloses:HH\\:mm}";
                }
                return null;

            case ResourceType.Car when opens is { } carOpens && closes is { } carCloses:
                var pickUpOk = startTime >= carOpens.ToTimeSpan() && startTime < carCloses.ToTimeSpan();
                var returnOk = localEnd.TimeOfDay >= carOpens.ToTimeSpan() && localEnd.TimeOfDay <= carCloses.ToTimeSpan();
                return pickUpOk && returnOk ? null
                    : $"{name} can be picked up and returned between {carOpens:HH\\:mm} and {carCloses:HH\\:mm}";

            case ResourceType.Catering when opens is { } kitchenOpens && closes is { } kitchenCloses:
                return startTime >= kitchenOpens.ToTimeSpan() && startTime < kitchenCloses.ToTimeSpan() ? null
                    : $"{name} delivers between {kitchenOpens:HH\\:mm} and {kitchenCloses:HH\\:mm}";

            default:
                return null;
        }
    }

    /// <summary>"3 days", "4 hours", "1 hour", "30 minutes".</summary>
    private static string Describe(TimeSpan span) => span switch
    {
        { TotalDays: >= 1 } when span.TotalDays % 1 == 0 => Plural((int)span.TotalDays, "day"),
        { TotalHours: >= 1 } when span.TotalHours % 1 == 0 => Plural((int)span.TotalHours, "hour"),
        _ => Plural((int)Math.Round(span.TotalMinutes), "minute"),
    };

    private static string Plural(int n, string unit) => n == 1 ? $"1 {unit}" : $"{n} {unit}s";
}
