using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ResourceBooking.Bookings;
using ResourceBooking.Data;

namespace ResourceBooking.Assistant;

/// <summary>
/// One global daily token budget (design: "Limits and failures"), counted per London day in token_usage. Before
/// each model call it is checked and an upper bound for the call is reserved; afterwards the reservation is settled
/// to the call's real usage, or kept if the call failed or was abandoned. A call can only start while the day is
/// under budget, so the day stops within one reservation per call racing the check.
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

    /// <summary>Adds tokens to today in one statement, so concurrent calls can't lose each other's counts.</summary>
    public async Task ChargeAsync(long tokens, CancellationToken ct) => await AddAsync(Today, tokens, ct);

    /// <summary>Charges an upper bound for a call now, and returns the day it was charged to.</summary>
    public async Task<DateOnly> ReserveAsync(long tokens, CancellationToken ct)
    {
        var today = Today;
        await AddAsync(today, tokens, ct);
        return today;
    }

    /// <summary>Replaces a reservation with what the call really used, on the day it was reserved.</summary>
    public async Task SettleAsync(DateOnly day, long reserved, long used, CancellationToken ct)
    {
        var refund = Math.Max(0, reserved) - Math.Max(0, used);
        if (refund < 0)
        {
            await AddAsync(day, -refund, ct);
        }
        else if (refund > 0)
        {
            await db.Database.ExecuteSqlAsync(
                $"UPDATE token_usage SET tokens = GREATEST(0, tokens - {refund}) WHERE day = {day}", ct);
        }
    }

    private async Task AddAsync(DateOnly day, long tokens, CancellationToken ct)
    {
        if (tokens <= 0)
        {
            return;
        }
        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO token_usage (day, tokens) VALUES ({day}, {tokens}) ON CONFLICT (day) DO UPDATE SET tokens = token_usage.tokens + {tokens}", ct);
    }
}
