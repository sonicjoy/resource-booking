using Microsoft.EntityFrameworkCore;
using ResourceBooking.Assistant;
using ResourceBooking.Auth;
using ResourceBooking.Bookings;
using ResourceBooking.Data;
using ResourceBooking.Shared;
using ResourceBooking.Shared.Dtos;

namespace ResourceBooking.Endpoints;

/// <summary>The assistant: send a message (rate-limited per user); admins read the traces.</summary>
public static class AssistantEndpoints
{
    public static RouteGroupBuilder MapAssistantEndpoints(this RouteGroupBuilder api)
    {
        var assistant = api.MapGroup("/assistant");

        assistant.MapPost("/messages", async (AssistantRequest request, HttpContext http, AssistantService service, CancellationToken ct) =>
        {
            if (ApiResults.Invalid(request) is { } invalid)
            {
                return invalid;
            }
            var result = await service.SendAsync(http.User.UserId()!.Value, request, ct);
            return result.Error is { } error ? ApiResults.Problem(error) : Results.Ok(result.Value);
        }).RequireRateLimiting(ApiGuards.AssistantLimit);

        var traces = assistant.MapGroup("/traces").RequireAuthorization(CookieSetup.AdminPolicy);

        traces.MapGet("/", async (DateOnly? from, DateOnly? to, AppDb db, TimeZoneInfo zone, CancellationToken ct) =>
        {
            if ((from is { } f && !CompanyTime.InRange(f)) || (to is { } t && !CompanyTime.InRange(t)))
            {
                return ApiResults.Problem(ApiError.BadRequest("Give dates before the year 9999"));
            }
            var steps = db.AssistantSteps.AsNoTracking();
            if (from is { } first)
            {
                var start = CompanyTime.StartOfDay(first, zone);
                steps = steps.Where(s => s.At >= start);
            }
            if (to is { } last)
            {
                var end = CompanyTime.StartOfDay(last.AddDays(1), zone);
                steps = steps.Where(s => s.At < end);
            }
            var rows = await steps
                .GroupBy(s => new { s.ConversationId, s.UserId })
                .Select(g => new
                {
                    g.Key.ConversationId,
                    g.Key.UserId,
                    Started = g.Min(s => s.At),
                    Last = g.Max(s => s.At),
                    Turns = g.Count(s => s.Kind == StepKind.UserMessage),
                    Steps = g.Count(),
                    Tokens = g.Sum(s => (s.InputTokens ?? 0) + (s.OutputTokens ?? 0)),
                })
                .OrderByDescending(r => r.Last).Take(200).ToListAsync(ct);
            var ids = rows.Select(r => r.UserId).Distinct().ToList();
            var names = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
            return Results.Ok(rows.Select(r => new TraceSummary(r.ConversationId, names.GetValueOrDefault(r.UserId, "A removed user"),
                CompanyTime.ToLocal(r.Started, zone), CompanyTime.ToLocal(r.Last, zone), r.Turns, r.Steps, r.Tokens)).ToList());
        });

        traces.MapGet("/{conversationId:guid}", async (Guid conversationId, AppDb db, TimeZoneInfo zone, CancellationToken ct) =>
        {
            var steps = await db.AssistantSteps.AsNoTracking().Where(s => s.ConversationId == conversationId).OrderBy(s => s.Seq).ToListAsync(ct);
            return steps.Count == 0
                ? ApiResults.Problem(ApiError.NotFound("No such conversation"))
                : Results.Ok(steps.Select(s => new TraceStep(s.Seq, s.Kind, CompanyTime.ToLocal(s.At, zone), s.Payload,
                    s.InputTokens, s.OutputTokens, s.DurationMs)).ToList());
        });

        return api;
    }
}
