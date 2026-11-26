namespace ResourceBooking.Shared;

/// <summary>The years the month view and Book links accept (design: month-view §3); the server and the client share them.</summary>
public static class CalendarRange
{
    public const int FirstYear = 2000;
    public const int LastYear = 2100;

    public static bool InRange(DateOnly date) => date.Year is >= FirstYear and <= LastYear;
}
