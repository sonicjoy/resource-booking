using ResourceBooking.Shared;

namespace ResourceBooking.Data.Entities;

public class Proposal
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public Guid UserId { get; set; }
    public int Turn { get; set; }
    public ProposalKind Kind { get; set; }
    public Guid? BookingId { get; set; }
    public required string Payload { get; set; }
    public ProposalStatus Status { get; set; }
    public bool ChangedBeforeConfirm { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
