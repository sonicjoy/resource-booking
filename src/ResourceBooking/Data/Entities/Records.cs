using ResourceBooking.Shared;

namespace ResourceBooking.Data.Entities;

public class AuditEvent
{
    public long Id { get; set; }
    public DateTimeOffset At { get; set; }

    /// <summary>Null when the system wrote the change (seeding, reseed).</summary>
    public Guid? ActorId { get; set; }

    public required string EntityType { get; set; }
    public Guid EntityId { get; set; }
    public required string Action { get; set; }

    /// <summary>JSON object mapping each changed field to [old, new].</summary>
    public required string Changes { get; set; }
}

public class Notice
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid BookingId { get; set; }
    public required string Message { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
}

public class AssistantStep
{
    public long Id { get; set; }
    public Guid ConversationId { get; set; }
    public Guid UserId { get; set; }
    public int Seq { get; set; }
    public StepKind Kind { get; set; }
    public required string Payload { get; set; }
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public int? DurationMs { get; set; }
    public DateTimeOffset At { get; set; }
}

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

public class TokenUsage
{
    public DateOnly Day { get; set; }
    public long Tokens { get; set; }
}
