using System.ComponentModel.DataAnnotations;

namespace ResourceBooking.Shared.Dtos;

/// <summary>A message to the assistant; no conversation id starts a new conversation.</summary>
public sealed class AssistantRequest
{
    public Guid? ConversationId { get; set; }

    [Required, StringLength(1000, MinimumLength = 1)]
    public string Text { get; set; } = "";
}

/// <summary>
/// A booking, change or cancellation the assistant proposes. Nothing is booked until the user confirms it in the
/// form, which sends the proposal's id with the request. <see cref="Request"/> is null for a cancellation.
/// </summary>
public sealed record ProposalDto(Guid Id, ProposalKind Kind, Guid? BookingId, BookingRequest? Request, string Summary);

/// <summary>The assistant's answer and the proposals still open in this conversation's latest turn.</summary>
public sealed record AssistantReply(Guid ConversationId, string Text, IReadOnlyList<ProposalDto> Proposals);

/// <summary>One conversation in the admin's trace list.</summary>
public sealed record TraceSummary(Guid ConversationId, string User, DateTime Started, DateTime Last, int Turns, int Steps, int Tokens);

/// <summary>One recorded step: a user message, a model call, a tool call or a reply, with what it carried.</summary>
public sealed record TraceStep(int Seq, StepKind Kind, DateTime At, string Payload, int? InputTokens, int? OutputTokens, int? DurationMs);
