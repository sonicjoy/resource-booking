using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ResourceBooking.Data;
using ResourceBooking.Data.Entities;
using ResourceBooking.Shared;

namespace ResourceBooking.Assistant;

/// <summary>
/// Writes one conversation's steps in order. Each kind has one writer: the service writes the user's message and
/// the reply, <see cref="TracingChatClient"/> each model call, and <see cref="TracedFunction"/> each tool call.
/// Steps are saved as they happen, so a turn that fails part-way still shows how far it got. Two messages sent at
/// once to one conversation interleave their steps rather than fail.
/// </summary>
public sealed partial class Trace(AppDb db, TimeProvider clock, Guid conversationId, Guid userId, int nextSeq)
{
    public Guid ConversationId { get; } = conversationId;

    public async Task AddAsync(StepKind kind, object payload, int? inputTokens = null, int? outputTokens = null, int? durationMs = null)
    {
        var json = NoNul(JsonSerializer.Serialize(payload, JsonSerializerOptions.Web));
        for (var attempt = 1; ; attempt++)
        {
            var step = new AssistantStep
            {
                Id = Guid.CreateVersion7(),
                ConversationId = ConversationId,
                UserId = userId,
                Seq = nextSeq++,
                Kind = kind,
                Payload = json,
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                DurationMs = durationMs,
                At = clock.GetUtcNow(),
            };
            db.AssistantSteps.Add(step);
            try
            {
                // Not the request's token: a step is kept even when the model call it records was cancelled.
                await db.SaveChangesAsync(CancellationToken.None);
                return;
            }
            catch (DbUpdateException e)
            {
                // A failed step must not stay tracked, or every later save in this request fails with it.
                db.Entry(step).State = EntityState.Detached;
                if (attempt >= 3 || e.InnerException is not PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
                {
                    throw;
                }
                // Another message in the same conversation took this number: continue after its steps.
                nextSeq = (await db.AssistantSteps.Where(x => x.ConversationId == ConversationId).MaxAsync(x => (int?)x.Seq) ?? 0) + 1;
            }
        }
    }

    /// <summary>PostgreSQL's jsonb refuses the \u0000 escape; a NUL in any text is stored as U+FFFD instead.</summary>
    private static string NoNul(string json) => NulEscape().Replace(json, @"$1�");

    /// <summary>A \u0000 escape not itself escaped: preceded by an even number of backslashes.</summary>
    [GeneratedRegex(@"(?<!\\)((?:\\\\)*)\\u0000")]
    private static partial Regex NulEscape();
}
