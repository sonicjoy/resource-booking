using System.Net;
using System.Net.Http.Json;
using ResourceBooking.Data;
using ResourceBooking.Shared;
using ResourceBooking.Shared.Dtos;
using ResourceBooking.Tests.Infrastructure;

namespace ResourceBooking.Tests.Api;

/// <summary>
/// GET /api/availability/month (design: month-view §3): one share per calendar day for one type, the same for
/// every caller. The seed's past bookings fall in Wednesday 18 to Tuesday 24 November 2026.
/// </summary>
public class MonthTests(DbFixture fixture) : IAsyncLifetime
{
    private ApiFactory _app = null!;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _app = await ApiFactory.CreateAsync(fixture);

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    private async Task<MonthOccupancy> MonthAsync(Guid user, string type = "MeetingRoom", int year = 2026, int month = 11)
    {
        var client = await _app.SignedInAsync(user);
        return (await client.GetFromJsonAsync<MonthOccupancy>($"/api/availability/month?year={year}&month={month}&type={type}", Ct))!;
    }

    [Fact]
    public async Task Every_day_of_the_month_is_there_in_order()
    {
        var month = await MonthAsync(Seed.AnaId, month: 2, type: "Car");

        Assert.Equal((2026, 2, ResourceType.Car), (month.Year, month.Month, month.Type));
        Assert.Equal(Enumerable.Range(1, 28).Select(d => new DateOnly(2026, 2, d)), month.Days.Select(d => d.Date));
    }

    [Fact]
    public async Task A_day_is_the_ratio_of_the_types_summed_hours()
    {
        var month = await MonthAsync(Seed.AnaId);

        // Rooms on the 18th: Thames and Severn open 08:00-18:00; Design review (Thames, 2 hours) is the day's only room booking.
        var day = month.Days.Single(d => d.Date == new DateOnly(2026, 11, 18));
        Assert.Equal((20.0, 2.0), (day.Available, day.Used));
        Assert.Equal(2.0 / 20, day.Share!.Value, 6);
    }

    [Fact]
    public async Task The_month_matches_the_report_for_the_same_days()
    {
        var sam = await _app.SignedInAsync(Seed.SamId);
        var report = (await sam.GetFromJsonAsync<List<UtilisationRow>>("/api/reports/utilisation?from=2026-11-18&to=2026-11-24", Ct))!
            .Where(r => r.Type == ResourceType.MeetingRoom).ToList();

        var week = (await MonthAsync(Seed.SamId)).Days.Where(d => d.Date >= new DateOnly(2026, 11, 18) && d.Date <= new DateOnly(2026, 11, 24)).ToList();

        Assert.Equal((report.Sum(r => r.Available), report.Sum(r => r.Used)), (week.Sum(d => d.Available), week.Sum(d => d.Used)));
    }

    [Fact]
    public async Task A_day_with_nothing_open_has_no_share()
    {
        await using (var db = _app.Db())
        {
            foreach (var room in db.Resources.Where(r => r.Type == ResourceType.MeetingRoom))
            {
                room.LastDay = new DateOnly(2026, 11, 10);
            }
            await db.SaveChangesAsync(Ct);
        }

        var month = await MonthAsync(Seed.AnaId);

        Assert.NotNull(month.Days.Single(d => d.Date == new DateOnly(2026, 11, 10)).Share);
        var closed = month.Days.Single(d => d.Date == new DateOnly(2026, 11, 11));
        Assert.Equal((0.0, 0.0, (double?)null), (closed.Available, closed.Used, closed.Share));
    }

    [Fact]
    public async Task Catering_counts_orders_over_units()
    {
        var month = await MonthAsync(Seed.AnaId, type: "Catering");

        // One kitchen with five units; the seed has one past order in the week of the 18th.
        Assert.All(month.Days, d => Assert.Equal(5.0, d.Available));
        Assert.Equal(1.0, month.Days.Sum(d => d.Used));
    }

    [Fact]
    public async Task Everyone_gets_the_same_body()
    {
        var ana = await _app.SignedInAsync(Seed.AnaId);
        var sam = await _app.SignedInAsync(Seed.SamId);
        const string url = "/api/availability/month?year=2026&month=11&type=Car";

        Assert.Equal(await ana.GetStringAsync(url, Ct), await sam.GetStringAsync(url, Ct));
    }

    [Theory]
    [InlineData("year=2026&month=0&type=Car")]
    [InlineData("year=2026&month=13&type=Car")]
    [InlineData("year=1999&month=12&type=Car")]
    [InlineData("year=2101&month=1&type=Car")]
    [InlineData("year=2026&month=12&type=Spaceship")]
    [InlineData("year=2026&month=12&type=7")]
    public async Task A_bad_month_year_or_type_is_400(string query)
    {
        var ana = await _app.SignedInAsync(Seed.AnaId);

        var response = await ana.GetAsync($"/api/availability/month?{query}", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Signed_out_is_401() =>
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _app.Client().GetAsync("/api/availability/month?year=2026&month=11&type=Car", Ct)).StatusCode);
}
