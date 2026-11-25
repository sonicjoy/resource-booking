namespace ResourceBooking.Data.Entities;

public class Notice
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid BookingId { get; set; }
    public required string Message { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
}
