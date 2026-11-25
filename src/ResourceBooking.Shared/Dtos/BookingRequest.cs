using System.ComponentModel.DataAnnotations;

namespace ResourceBooking.Shared.Dtos;

/// <summary>
/// A booking as the client sends it. Times are company-local wall-clock times with no offset
/// ("2026-11-26T14:00"); the server converts them with the configured time zone. For a catering
/// order the server takes the day from <see cref="DeliveryAt"/> and ignores Start and End.
/// The client never chooses a kitchen unit.
/// </summary>
public class BookingRequest
{
    [Required]
    public Guid? ResourceId { get; set; }

    [Required]
    public DateTime? Start { get; set; }

    [Required]
    public DateTime? End { get; set; }

    [Range(1, 1000)]
    public int Attendees { get; set; } = 1;

    [StringLength(100)]
    public string? Title { get; set; }

    public DateTime? DeliveryAt { get; set; }

    public Guid? DeliveryRoomId { get; set; }

    public Guid? ProposalId { get; set; }
}
