using System.Globalization;
using System.Net.Http.Json;
using Microsoft.Playwright;
using ResourceBooking.Data;
using static Microsoft.Playwright.Assertions;

namespace ResourceBooking.E2E;

/// <summary>
/// CUJ 7 (design: month-view CUJs 1-3): Ana finds a quiet week in Month; Sam sees who holds Thames on Book; Ben
/// sees no one else's details. Dates count from today in London.
/// </summary>
[Collection(E2ECollection.Name)]
public class Cuj7MonthViewTests(AppFixture app) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await app.ReseedAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static DateOnly Tomorrow => Pages.LondonToday().AddDays(1);

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private async Task BookAsync(Guid user, Guid resource, DateOnly day, int from, int to, string title)
    {
        using var api = await app.ApiAsAsync(user);
        (await api.PostAsJsonAsync("/api/bookings",
            new { resourceId = resource, start = $"{Iso(day)}T{from:00}:00", end = $"{Iso(day)}T{to:00}:00", attendees = 2, title }, Ct)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Ana_finds_a_quiet_week_next_month_and_opens_a_busy_day()
    {
        var next = Pages.LondonToday().AddMonths(1);
        var third = new DateOnly(next.Year, next.Month, 3);
        await BookAsync(Seed.AnaId, Seed.ThamesId, third, 10, 12, "Planning day");
        await BookAsync(Seed.AnaId, Seed.SevernId, third.AddDays(1), 9, 13, "Workshop");

        var ana = await app.SignInAsync("Ana");
        await ana.GoToAsync("Month");
        await ana.Button("Next month").ClickAsync();

        // Thames and Severn are open 08:00-18:00, 20 room hours a day: 2 hours is 10%, 4 hours is 20%.
        await Expect(ana.DayLink(third)).ToHaveAttributeAsync("aria-label", $"{third:dddd d MMMM}, 10% of room hours booked");
        await Expect(ana.DayLink(third.AddDays(1))).ToContainTextAsync("20% of room hours booked");
        for (var d = 8; d <= 14; d++)
        {
            await Expect(ana.DayLink(new DateOnly(next.Year, next.Month, d))).ToContainTextAsync("0% of room hours booked");
        }

        await ana.DayLink(third).ClickAsync();
        await Expect(ana.Locator(".day-label")).ToContainTextAsync(third.ToString("dddd d MMMM yyyy", CultureInfo.InvariantCulture));
        await Expect(ana.GetByRole(AriaRole.Tab, new() { Name = "Rooms", Exact = true })).ToHaveAttributeAsync("aria-selected", "true");
        await Expect(ana.Cell("Thames", "10:00")).ToHaveAttributeAsync("aria-label", "Thames, 10:00, your booking: Planning day");
    }

    [Fact]
    public async Task Sam_sees_who_holds_Thames_and_never_the_title()
    {
        await BookAsync(Seed.AnaId, Seed.ThamesId, Tomorrow, 10, 11, "Board prep");

        var sam = await app.SignInAsync("Sam");
        await sam.GoToAsync("Month");
        if (Tomorrow.Month != Pages.LondonToday().Month)
        {
            await sam.Button("Next month").ClickAsync();
        }
        await sam.DayLink(Tomorrow).ClickAsync();

        await Expect(sam.Cell("Thames", "10:00")).ToHaveAttributeAsync("aria-label", "Thames, 10:00, booked by Ana");
        await Expect(sam.Cell("Thames", "10:00")).ToHaveTextAsync("Booked: Ana");
        await Expect(sam.GetByText("Board prep")).ToHaveCountAsync(0);
        using var api = await app.ApiAsAsync(Seed.SamId);
        var body = await api.GetStringAsync($"/api/availability?date={Iso(Tomorrow)}&type=MeetingRoom", Ct);
        Assert.DoesNotContain("Board prep", body);
    }

    [Fact]
    public async Task Ben_sees_Booked_with_no_name_and_his_own_titles()
    {
        await BookAsync(Seed.AnaId, Seed.ThamesId, Tomorrow, 10, 11, "Board prep");

        var ben = await app.SignInAsync("Ben");
        await ben.GotoAsync($"{app.BaseUrl}/?date={Iso(Tomorrow)}&type=MeetingRoom");

        await Expect(ben.Cell("Thames", "10:00")).ToHaveAttributeAsync("aria-label", "Thames, 10:00, booked");
        await Expect(ben.Cell("Thames", "10:00")).ToHaveTextAsync("Booked");
        await ben.OpenTabAsync("Cars");
        await Expect(ben.Cell("Pool Car 1", "09:00")).ToHaveAttributeAsync("aria-label", "Pool Car 1, 09:00, your booking: Site visit");
    }

    [Fact]
    public async Task The_catering_tab_says_what_its_numbers_count()
    {
        var ana = await app.SignInAsync("Ana");
        await ana.GotoAsync($"{app.BaseUrl}/?date={Iso(Tomorrow.AddDays(7))}&type=Catering");

        await Expect(ana.GetByText("Main Kitchen: 5 of 5 orders left on this day")).ToBeVisibleAsync();
        await Expect(ana.GetByText("Takes 5 orders a day, up to 25 portions each, delivered 08:00 to 16:00.")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Leaving_Book_from_another_tab_lands_on_Month_and_stays_there()
    {
        var ana = await app.SignInAsync("Ana");
        await ana.OpenTabAsync("Cars");
        await Expect(ana).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("type=Car$"));

        await ana.GoToAsync("Month");
        await ana.WaitForTimeoutAsync(1000);

        await Expect(ana).ToHaveURLAsync($"{app.BaseUrl}/month");
        await Expect(ana.GetByRole(AriaRole.Tab, new() { Name = "Rooms", Exact = true })).ToHaveAttributeAsync("aria-selected", "true");
    }

    [Fact]
    public async Task A_link_with_a_bad_date_or_type_opens_today_on_rooms()
    {
        var ana = await app.SignInAsync("Ana");
        await ana.GotoAsync($"{app.BaseUrl}/?date=someday&type=Spaceship");

        await Expect(ana.Locator(".day-label")).ToContainTextAsync(Pages.LondonToday().ToString("dddd d MMMM yyyy", CultureInfo.InvariantCulture));
        await Expect(ana.GetByRole(AriaRole.Tab, new() { Name = "Rooms", Exact = true })).ToHaveAttributeAsync("aria-selected", "true");
    }
}
