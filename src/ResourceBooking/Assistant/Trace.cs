using System.Text.Json;
using ResourceBooking.Data;
using ResourceBooking.Data.Entities;
using ResourceBooking.Shared;

namespace ResourceBooking.Assistant;

/// <summary>
/// Writes one conversation's steps in order. Each kind has one writer: the service writes the user's message and
/// the reply, <see cref="TracingChatClient"/> each model call, and <see cref="TracedFunction"/> each tool call.
/// Steps are saved as they happen, so a turn that fails part-way still shows how far it got.
/// </summary>
public sealed class Trace(AppDb db, TimeProvider clock, Guid conversationId, Guid userId, int nextSeq)
{
    public Guid ConversationId { get; } = conversationId;

    public async Task AddAsync(StepKind kind, object payload, int? inputTokens = null, int? outputTokens = null, int? durationMs = null)
    {
        db.AssistantSteps.Add(new AssistantStep
        {
            Id = Guid.CreateVersion7(),
            ConversationId = ConversationId,
            UserId = userId,
            Seq = nextSeq++,
            Kind = kind,
            Payload = JsonSerializer.Serialize(payload, JsonSerializerOptions.Web),
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            DurationMs = durationMs,
            At = clock.GetUtcNow(),
        });
        // Not the request's token: a step is kept even when the model call it records was cancelled.
        await db.SaveChangesAsync(CancellationToken.None);
    }
}
