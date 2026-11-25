using ResourceBooking.Shared;

namespace ResourceBooking.Data.Entities;

public class AssistantStep
{
    public Guid Id { get; set; }
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
