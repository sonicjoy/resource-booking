using ResourceBooking.Shared;

namespace ResourceBooking.Data.Entities;

public class Booking
{
    public Guid Id { get; set; }
    public Guid ResourceId { get; set; }
    public Guid UserId { get; set; }

    /// <summary>Kitchen unit for a catering order; 0 for every other resource.</summary>
    public int UnitNo { get; set; }

    public string? Title { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset EndsAt { get; set; }

    /// <summary>EndsAt plus the resource's turnaround gap; the no-overlap constraint uses this.</summary>
    public DateTimeOffset BlockedUntil { get; set; }

    /// <summary>Attendees, passengers, or portions for a catering order.</summary>
    public int Attendees { get; set; }

    public DateTimeOffset? DeliveryAt { get; set; }
    public Guid? DeliveryRoomId { get; set; }
    public BookingStatus Status { get; set; }
    public string? CancelReason { get; set; }
    public Guid? ProposalId { get; set; }

    public Resource? Resource { get; set; }
    public User? User { get; set; }
}
