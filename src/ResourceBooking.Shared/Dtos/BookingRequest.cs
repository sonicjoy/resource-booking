using System.ComponentModel.DataAnnotations;

namespace ResourceBooking.Shared.Dtos;

/// <summary>
/// A booking as the client sends it. Times are company-local wall-clock times with no offset
/// ("2026-11-26T14:00"); the server converts them with the configured time zone. A room or car
/// booking needs Start and End; a catering order needs only <see cref="DeliveryAt"/>, from which
/// the server takes the day. The client never chooses a kitchen unit.
/// </summary>
public class BookingRequest : IValidatableObject
{
    [Required]
    public Guid? ResourceId { get; set; }

    public DateTime? Start { get; set; }

    public DateTime? End { get; set; }

    [Range(1, 1000)]
    public int Attendees { get; set; } = 1;

    [StringLength(100)]
    public string? Title { get; set; }

    public DateTime? DeliveryAt { get; set; }

    public Guid? DeliveryRoomId { get; set; }

    public Guid? ProposalId { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        // PostgreSQL text can't hold a NUL, so one would fail the save as a server error.
        if (Title?.Contains('\0', StringComparison.Ordinal) == true)
        {
            yield return new ValidationResult("The title can't contain a NUL character.", [nameof(Title)]);
        }
        if (DeliveryAt is not null)
        {
            yield break;
        }
        if (Start is null)
        {
            yield return new ValidationResult("Choose a start time.", [nameof(Start)]);
        }
        if (End is null)
        {
            yield return new ValidationResult("Choose an end time.", [nameof(End)]);
        }
        else if (Start is not null && End <= Start)
        {
            yield return new ValidationResult("The booking must end after it starts.", [nameof(End)]);
        }
    }
}
