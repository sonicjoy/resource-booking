using System.Net;
using System.Net.Http.Json;
using CsCheck;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ResourceBooking.Assistant;
using ResourceBooking.Data;
using ResourceBooking.Tests.Infrastructure;

namespace ResourceBooking.Tests.Assistant;

/// <summary>
/// The daily token budget at tier high (design: "Limits and failures"). Level 4: random sequences of charges,
/// clock moves and checks against a model of tokens per London day, starting near midnight and near both clock
/// changes. Then: concurrent charges lose nothing, and through the endpoint a day's spend stops within one
/// call of the budget.
/// </summary>
public class TokenBudgetModelTests(DbFixture fixture) : IAsyncLifetime
{
    private const long Budget = 500;
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    private ApiFactory _app = null!;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _app = await ApiFactory.CreateAsync(fixture,
        settings: new Dictionary<string, string> { ["Assistant:DailyTokenBudget"] = $"{Budget}", ["Assistant:MessagesPerMinute"] = "0" });

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    /// <summary>Charge some tokens (zero or fewer is ignored), move the clock on, or just check.</summary>
    public sealed record Op(string Kind, long Amount)
    {
        public override string ToString() => $"{Kind}({Amount})";
    }

    private static readonly Gen<Op> AnyOp = Gen.OneOf(
        Gen.Long[-5, 300].Select(n => new Op("charge", n)),
        Gen.Long[0, 1500].Select(minutes => new Op("advance", minutes)),
        Gen.Const(new Op("check", 0)));

    /// <summary>
    /// 23:00 London on an ordinary day, the evening before the spring change, or the evening before the autumn
    /// change (the last Sundays of March and October), in the given year. The fake clock only goes forward, so
    /// each sample starts in a later year.
    /// </summary>
    private static DateTimeOffset Start(int kind, int year)
    {
        static DateTime LastSunday(int year, int month)
        {
            var day = new DateTime(year, month, DateTime.DaysInMonth(year, month));
            return day.AddDays(-(int)day.DayOfWeek);
        }
        return kind switch
        {
            0 => new DateTimeOffset(year, 11, 25, 23, 0, 0, TimeSpan.Zero),
            1 => new DateTimeOffset(LastSunday(year, 3).AddHours(-1), TimeSpan.Zero),
            _ => new DateTimeOffset(LastSunday(year, 10).AddHours(-2), TimeSpan.Zero),
        };
    }

    private static DateOnly LondonDay(DateTimeOffset t) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(t, London).DateTime);

    [Fact]
    public async Task Every_sequence_matches_a_count_per_London_day()
    {
        await Gen.Select(Gen.Int[0, 2], AnyOp.List[1, 12]).SampleAsync(async (start, ops) =>
        {
            await using var scope = _app.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            await db.Database.ExecuteSqlRawAsync("TRUNCATE token_usage", Ct);
            _app.Clock.SetUtcNow(Start(start, _app.Clock.GetUtcNow().Year + 1));
            var budget = scope.ServiceProvider.GetRequiredService<TokenBudget>();
            var model = new Dictionary<DateOnly, long>();

            foreach (var op in ops)
            {
                var today = LondonDay(_app.Clock.GetUtcNow());
                switch (op.Kind)
                {
                    case "charge":
                        await budget.ChargeAsync(op.Amount, Ct);
                        if (op.Amount > 0)
                        {
                            model[today] = model.GetValueOrDefault(today) + op.Amount;
                        }
                        break;
                    case "advance":
                        _app.Clock.Advance(TimeSpan.FromMinutes(op.Amount));
                        break;
                }

                today = LondonDay(_app.Clock.GetUtcNow());
                Assert.Equal(model.GetValueOrDefault(today), await budget.UsedTodayAsync(Ct));
                Assert.Equal(model.GetValueOrDefault(today) < Budget, await budget.HasRoomAsync(Ct));
            }

            var rows = await db.TokenUsage.AsNoTracking().ToDictionaryAsync(t => t.Day, t => t.Tokens, Ct);
            Assert.Equal(model.OrderBy(kv => kv.Key), rows.OrderBy(kv => kv.Key));
        }, iter: 60, threads: 1);
    }

    [Fact]
    public async Task Concurrent_charges_lose_nothing()
    {
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<AppDb>().Database.ExecuteSqlRawAsync("TRUNCATE token_usage", Ct);
        }

        await Task.WhenAll(Enumerable.Range(1, 40).Select(async i =>
        {
            await using var scope = _app.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<TokenBudget>().ChargeAsync(i, Ct);
        }));

        await using var db = _app.Db();
        Assert.Equal(40 * 41 / 2, (await db.TokenUsage.SingleAsync(Ct)).Tokens);
    }

    [Fact]
    public async Task Through_the_endpoint_a_day_stops_within_one_call_of_the_budget()
    {
        var ana = await _app.SignedInAsync(Seed.AnaId);
        await Gen.Long[1, 260].SampleAsync(async perCall =>
        {
            await using (var scope = _app.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<AppDb>().Database.ExecuteSqlRawAsync("TRUNCATE token_usage", Ct);
            }
            _app.Model.TokensPerCall = (int)perCall;
            _app.Model.Calls.Clear();

            // Each "hello" is one model call; calls go on while the day's spend is under the budget.
            var expectedCalls = (int)((Budget + perCall - 1) / perCall);
            for (var i = 0; i < expectedCalls; i++)
            {
                Assert.Equal(HttpStatusCode.OK, (await ana.PostAsJsonAsync("/api/assistant/messages", new { text = "hello" }, Ct)).StatusCode);
            }
            var refused = await ana.PostAsJsonAsync("/api/assistant/messages", new { text = "hello" }, Ct);

            Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
            Assert.Equal(expectedCalls, _app.Model.Calls.Count);
            await using var db = _app.Db();
            var spent = (await db.TokenUsage.SingleAsync(Ct)).Tokens;
            Assert.InRange(spent, Budget, Budget + perCall - 1);
        }, iter: 12, threads: 1);
    }

    [Fact]
    public void The_budget_comes_from_config()
    {
        Assert.Equal(Budget, _app.Services.GetRequiredService<IOptions<AssistantOptions>>().Value.DailyTokenBudget);
    }
}
