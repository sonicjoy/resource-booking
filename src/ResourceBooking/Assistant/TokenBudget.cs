using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ResourceBooking.Bookings;
using ResourceBooking.Data;

namespace ResourceBooking.Assistant;

/// <summary>
/// One global daily token budget (design: "Limits and failures"), counted per London day in token_usage. It is
/// checked before each model call and charged after it, so each call already in flight when the budget runs out
/// can overshoot it by that call's size; one at a time, the day stops within one call of the budget.
/// </summary>
public sealed class TokenBudget(AppDb db, TimeProvider clock, TimeZoneInfo zone, IOptions<AssistantOptions> options)
{
    private DateOnly Today => CompanyTime.LocalDate(clock.GetUtcNow(), zone);

    public async Task<long> UsedTodayAsync(CancellationToken ct)
    {
        var today = Today;
        return await db.TokenUsage.Where(t => t.Day == today).Select(t => t.Tokens).SingleOrDefaultAsync(ct);
    }

    public async Task<bool> HasRoomAsync(CancellationToken ct) => await UsedTodayAsync(ct) < options.Value.DailyTokenBudget;

    /// <summary>Adds a call's tokens in one statement, so concurrent calls can't lose each other's counts.</summary>
    public async Task ChargeAsync(long tokens, CancellationToken ct)
    {
        if (tokens <= 0)
        {
            return;
        }
        var today = Today;
        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO token_usage (day, tokens) VALUES ({today}, {tokens}) ON CONFLICT (day) DO UPDATE SET tokens = token_usage.tokens + {tokens}", ct);
    }
}
