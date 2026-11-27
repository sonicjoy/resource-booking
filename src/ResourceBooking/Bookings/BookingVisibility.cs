using ResourceBooking.Data.Entities;

namespace ResourceBooking.Bookings;

/// <summary>
/// What a caller sees of a booking on the Book grid (design: month-view §1): a title goes only to its owner,
/// and the holder's name only to an admin looking at someone else's booking. <see cref="Booking.User"/> must be loaded.
/// </summary>
public static class BookingVisibility
{
    public static (bool Mine, string? Title, string? Owner) Project(Booking b, Guid callerId, bool isAdmin)
    {
        var mine = b.UserId == callerId;
        return mine ? (true, b.Title, null) : (false, null, isAdmin ? b.User!.DisplayName : null);
    }
}
