namespace ResourceBooking.Client.Services;

/// <summary>Counts in words for status messages.</summary>
public static class Plural
{
    public static string Bookings(int n) => n == 1 ? "1 booking" : $"{n} bookings";
}
