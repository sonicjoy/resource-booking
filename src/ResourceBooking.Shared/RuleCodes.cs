namespace ResourceBooking.Shared;

/// <summary>Codes for the booking rules that <c>BookingRules.Check</c> enforces (HTTP 422).</summary>
public static class RuleCodes
{
    public const string InPast = "IN_PAST";
    public const string TooSoon = "TOO_SOON";
    public const string TooFarAhead = "TOO_FAR_AHEAD";
    public const string OutsideOpeningHours = "OUTSIDE_OPENING_HOURS";
    public const string TooShort = "TOO_SHORT";
    public const string TooLong = "TOO_LONG";
    public const string OverCapacity = "OVER_CAPACITY";
    public const string ResourceRetired = "RESOURCE_RETIRED";
    public const string UserLimit = "USER_LIMIT";
    public const string KitchenFull = "KITCHEN_FULL";
}
