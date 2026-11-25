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
public sealed record RuleContext(int UserFutureCount, int? FreeUnit);

public sealed record RuleViolation(string Code, string Message);

/// <summary>
/// Every booking rule except overlaps, which the database refuses. One method, no I/O.
/// To add a rule: a column on <see cref="Resource"/>, a <see cref="RuleField"/> member listed for the
/// types it applies to in <see cref="RuleFields"/>, one check here, a code in <see cref="RuleCodes"/>, and a test.
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

        // The time rules look at when the resource is needed: for rooms and cars the start,
        // for a catering order the delivery (the order itself holds its unit for the whole day).
        var ruleTime = isKitchen
            ? candidate.DeliveryAt ?? throw new ArgumentException("A catering order needs a delivery time.", nameof(candidate))
            : candidate.Start;
        var localRuleTime = TimeZoneInfo.ConvertTime(ruleTime, zone).DateTime;
        var localEnd = TimeZoneInfo.ConvertTime(candidate.End, zone).DateTime;
        var localToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);

        if (candidate.End <= candidate.Start)
        {
            Refuse(RuleCodes.EndBeforeStart, "The booking must end after it starts");
        }

        if (ruleTime < now)
        {
            Refuse(RuleCodes.InPast, "That time has already passed");
        }
        else if (applies.Contains(RuleField.MinNotice) && resource.MinNotice is { } notice && ruleTime - now < notice)
        {
            Refuse(RuleCodes.TooSoon, $"{name} needs {Notice(notice)}");
        }

        // The window covers the whole booking, so a car can't be kept past it.
        var lastLocalDay = DateOnly.FromDateTime(isKitchen ? localRuleTime : localEnd);
        if (applies.Contains(RuleField.MaxDaysAhead) && resource.MaxDaysAhead is { } daysAhead
            && (long)lastLocalDay.DayNumber - localToday.DayNumber > daysAhead)
        {
            Refuse(RuleCodes.TooFarAhead, $"{name} can be booked up to {daysAhead} days ahead");
        }

        if (OpeningHoursMessage(resource, applies.Contains(RuleField.OpeningHours), localRuleTime, localEnd) is { } hours)
        {
            Refuse(RuleCodes.OutsideOpeningHours, hours);
        }

        var length = candidate.End - candidate.Start;
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

        if (resource.LastDay is { } lastDay && IsAfterLastDay(lastDay))
        {
            Refuse(RuleCodes.ResourceRetired,
                $"{name} can't be booked after {lastDay.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}");
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

        // A kitchen order is for one day; a room or car may not run past the end of the last day.
        bool IsAfterLastDay(DateOnly lastDay) => isKitchen
            ? DateOnly.FromDateTime(localRuleTime) > lastDay
            : lastDay < DateOnly.MaxValue && candidate.End > StartOfDayUtc(lastDay.AddDays(1), zone);
    }

    private static string? OpeningHoursMessage(Resource resource, bool hoursApply, DateTime localStart, DateTime localEnd)
    {
        var opens = hoursApply ? resource.OpensAt?.ToTimeSpan() : null;
        var closes = hoursApply ? resource.ClosesAt?.ToTimeSpan() : null;
        var startTime = localStart.TimeOfDay;

        switch (resource.Type)
        {
            case ResourceType.MeetingRoom:
                // Ending exactly at midnight still counts as the same day.
                TimeSpan? endTime = localEnd.Date == localStart.Date ? localEnd.TimeOfDay
                    : localEnd.Date == localStart.Date.AddDays(1) && localEnd.TimeOfDay == TimeSpan.Zero ? TimeSpan.FromDays(1)
                    : null;
                if (endTime is null)
                {
                    return $"{resource.Name} bookings must start and end on the same day";
                }
                if (startTime < opens)
                {
                    return $"{resource.Name} opens at {resource.OpensAt:HH\\:mm}";
                }
                if (endTime > closes)
                {
                    return $"{resource.Name} closes at {resource.ClosesAt:HH\\:mm}";
                }
                return null;

            case ResourceType.Car:
                // Pick-up must be before closing; return may be exactly at closing.
                var pickUpOk = !(startTime < opens) && !(startTime >= closes);
                var returnOk = !(localEnd.TimeOfDay < opens) && !(localEnd.TimeOfDay > closes);
                return pickUpOk && returnOk ? null : $"{resource.Name} can be picked up and returned {Window(resource)}";

            case ResourceType.Catering:
                var deliveryOk = !(startTime < opens) && !(startTime >= closes);
                return deliveryOk ? null : $"{resource.Name} delivers {Window(resource)}";

            default:
                return null;
        }
    }

    /// <summary>"between 07:00 and 19:00", "from 07:00" or "until 19:00".</summary>
    private static string Window(Resource resource) => (resource.OpensAt, resource.ClosesAt) switch
    {
        ({ } opens, { } closes) => $"between {opens:HH\\:mm} and {closes:HH\\:mm}",
        ({ } opens, null) => $"from {opens:HH\\:mm}",
        (null, { } closes) => $"until {closes:HH\\:mm}",
        _ => "at any time",
    };

    /// <summary>The UTC instant a local day begins; where midnight is skipped by a clock change, the first valid moment.</summary>
    private static DateTimeOffset StartOfDayUtc(DateOnly day, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue);
        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(30);
        }
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone));
    }

    /// <summary>"1 hour's notice", "2 hours' notice".</summary>
    private static string Notice(TimeSpan span)
    {
        var amount = Describe(span);
        return amount.EndsWith('s') ? $"{amount}' notice" : $"{amount}'s notice";
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
