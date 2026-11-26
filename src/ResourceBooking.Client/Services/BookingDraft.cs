using ResourceBooking.Shared;
using ResourceBooking.Shared.Dtos;

namespace ResourceBooking.Client.Services;

/// <summary>What the booking form is filling in: a new booking, a new catering order, or an edit of either.</summary>
public sealed class BookingDraft
{
    public Guid? BookingId { get; set; }
    public required Guid ResourceId { get; set; }
    public required string ResourceName { get; set; }
    public required ResourceType Type { get; set; }
    public DateOnly Date { get; set; }

    /// <summary>The day a booking ends: later than <see cref="Date"/> only for a car held over several days.</summary>
    public DateOnly EndDate { get; set; }
    public TimeOnly Start { get; set; }
    public TimeOnly End { get; set; }
    public int Attendees { get; set; } = 1;
    public string? Title { get; set; }
    public Guid? DeliveryRoomId { get; set; }

    /// <summary>The assistant's proposal this draft came from, sent so the booking is linked to it.</summary>
    public Guid? ProposalId { get; set; }

    public bool IsCatering => Type == ResourceType.Catering;

    public BookingRequest ToRequest() => IsCatering
        ? new BookingRequest { ResourceId = ResourceId, DeliveryAt = Date.ToDateTime(Start), DeliveryRoomId = DeliveryRoomId, Attendees = Attendees, Title = Blank(Title), ProposalId = ProposalId }
        : new BookingRequest { ResourceId = ResourceId, Start = Date.ToDateTime(Start), End = EndDate.ToDateTime(End), Attendees = Attendees, Title = Blank(Title), ProposalId = ProposalId };

    public static BookingDraft From(BookingDto booking) => new()
    {
        BookingId = booking.Id,
        ResourceId = booking.ResourceId,
        ResourceName = booking.ResourceName,
        Type = booking.ResourceType,
        Date = DateOnly.FromDateTime(booking.DeliveryAt ?? booking.Start),
        Start = TimeOnly.FromDateTime(booking.DeliveryAt ?? booking.Start),
        End = TimeOnly.FromDateTime(booking.End),
        EndDate = DateOnly.FromDateTime(booking.DeliveryAt ?? booking.End),
        Attendees = booking.Attendees,
        Title = booking.Title,
        DeliveryRoomId = booking.DeliveryRoomId,
    };

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
