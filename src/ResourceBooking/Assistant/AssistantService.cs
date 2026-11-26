using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using ResourceBooking.Admin;
using ResourceBooking.Bookings;
using ResourceBooking.Data;
using ResourceBooking.Shared;
using ResourceBooking.Shared.Dtos;

namespace ResourceBooking.Assistant;

/// <summary>
/// One turn of a conversation (design: "Assistant"): checks the conversation, turn cap and budget, supersedes the
/// last turn's open proposals, rebuilds the history, and runs the model inside function invocation (at most
/// <see cref="AssistantOptions.MaxIterations"/> calls) with every step traced. The model never books.
/// </summary>
public sealed class AssistantService(
    AppDb db, IChatClient model, BookingService bookings, AvailabilityService availability, TokenBudget budget,
    TimeProvider clock, TimeZoneInfo zone, IOptions<AssistantOptions> options, ILogger<AssistantService> log)
{
    public const string Unavailable = "Sorry, the assistant couldn't answer just now. You can still book with the forms.";
    public const string NotSetUp = "The assistant isn't set up here: no model is configured. You can still book with the forms.";

    public async Task<AdminResult<AssistantReply>> SendAsync(Guid userId, AssistantRequest request, CancellationToken ct)
    {
        var settings = options.Value;
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId, ct);
        var conversationId = request.ConversationId ?? Guid.CreateVersion7();
        var steps = await db.AssistantSteps.AsNoTracking().Where(s => s.ConversationId == conversationId).OrderBy(s => s.Seq).ToListAsync(ct);
        if (request.ConversationId is not null && (steps.Count == 0 || steps.Any(s => s.UserId != userId)))
        {
            return ApiError.NotFound("No such conversation");
        }
        var turns = steps.Count(s => s.Kind == StepKind.UserMessage);
        if (turns >= settings.MaxTurns)
        {
            return new ApiError(StatusCodes.Status422UnprocessableEntity, "TURN_LIMIT",
                $"A conversation holds {settings.MaxTurns} messages; start a new one");
        }
        if (!await budget.HasRoomAsync(ct))
        {
            return Exhausted;
        }

        await db.Proposals.Where(p => p.ConversationId == conversationId && p.Status == ProposalStatus.Open)
            .ExecuteUpdateAsync(p => p.SetProperty(x => x.Status, ProposalStatus.Superseded), ct);
        var turn = turns + 1;
        var trace = new Trace(db, clock, conversationId, userId, (steps.Count == 0 ? 0 : steps.Max(s => s.Seq)) + 1);
        await trace.AddAsync(StepKind.UserMessage, request.Text);

        var messages = new List<ChatMessage> { new(ChatRole.System, SystemPrompt(user.DisplayName)) };
        messages.AddRange(steps.Where(s => s.Kind is StepKind.UserMessage or StepKind.Reply)
            .Select(s => new ChatMessage(s.Kind == StepKind.UserMessage ? ChatRole.User : ChatRole.Assistant, JsonSerializer.Deserialize<string>(s.Payload))));
        messages.Add(new ChatMessage(ChatRole.User, request.Text));

        var tools = new AssistantTools(db, bookings, availability, clock, zone, userId, conversationId, turn);
        // Not disposed: the pipeline wraps the app's singleton model, which outlives this request.
        var client = new ChatClientBuilder(model)
            .UseFunctionInvocation(configure: f => f.MaximumIterationsPerRequest = settings.MaxIterations)
            .Use(inner => new TracingChatClient(inner, trace, budget, settings.MaxOutputTokens, seen: messages.Count - 1))
            .Build();

        string text;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        try
        {
            var response = await client.GetResponseAsync(messages, new ChatOptions { Tools = tools.Create(trace) }, timeout.Token);
            text = string.IsNullOrWhiteSpace(response.Text) ? "Done." : response.Text;
        }
        catch (BudgetExhaustedException)
        {
            await trace.AddAsync(StepKind.Reply, "The daily budget ran out during this message.");
            return Exhausted;
        }
        catch (AssistantUnavailableException)
        {
            text = NotSetUp;
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            log.LogWarning(e, "Assistant turn failed for conversation {ConversationId}", conversationId);
            text = Unavailable;
        }
        await trace.AddAsync(StepKind.Reply, text);

        var open = await db.Proposals.AsNoTracking()
            .Where(p => p.ConversationId == conversationId && p.Turn == turn && p.Status == ProposalStatus.Open)
            .OrderBy(p => p.CreatedAt).ThenBy(p => p.Id).ToListAsync(CancellationToken.None);
        var proposals = new List<ProposalDto>();
        foreach (var p in open)
        {
            var payload = p.Kind == ProposalKind.Cancel ? null : JsonSerializer.Deserialize<BookingRequest>(p.Payload, JsonSerializerOptions.Web);
            var summary = payload is null
                ? await tools.CancelSummaryAsync(p.BookingId!.Value, CancellationToken.None)
                : await tools.SummaryAsync(payload, CancellationToken.None);
            proposals.Add(new ProposalDto(p.Id, p.Kind, p.BookingId, payload, summary));
        }
        return new AssistantReply(conversationId, text, proposals);
    }

    private static readonly ApiError Exhausted = new(StatusCodes.Status429TooManyRequests, "BUDGET_EXHAUSTED",
        "The assistant's daily budget is spent; book with the forms, or try again tomorrow");

    private string SystemPrompt(string name)
    {
        var now = CompanyTime.ToLocal(clock.GetUtcNow(), zone);
        return $"""
            You help {name} book meeting rooms, pool cars and catering at the company. Today is {now.ToString("dddd d MMMM yyyy", CultureInfo.InvariantCulture)}, and it is {now:HH:mm} in London; every time you send or receive is London wall-clock time, written like 2026-11-26T09:00.
            You cannot book. Use find_free_resources and get_my_bookings to look, then propose_booking, propose_change or propose_cancellation to offer something; the user confirms each proposal in a form.
            A catering order is one kitchen order of up to its portions, delivered to a meeting room at a time; propose it with deliveryAt and deliveryRoomId, not start and end.
            If a tool says "Not proposed", explain why in a sentence and offer an alternative. Keep replies short.
            """;
    }
}
