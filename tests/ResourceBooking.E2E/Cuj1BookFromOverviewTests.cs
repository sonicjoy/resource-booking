using ResourceBooking.Data;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace ResourceBooking.E2E;

/// <summary>CUJ 1: an employee books a room and its catering from the overview; a colleague's clash offers alternatives.</summary>
[Collection(E2ECollection.Name)]
public class Cuj1BookFromOverviewTests(AppFixture app) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await app.ReseedAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Ana_books_Thames_and_lunch_and_Ben_is_offered_alternatives()
    {
        var ana = await app.SignInAsync("Ana");
        await ana.GoToTomorrowAsync();
        await ana.Cell("Thames", "14:00").ClickAsync();
        await ana.FillFieldAsync("Attendees", "6");
        await ana.ConfirmAsync();

        await Expect(ana.Locator("[role=status]")).ToContainTextAsync("Booked Thames");
        await ana.Button("Add catering").ClickAsync();
        await Expect(ana.Form().GetByText("Main Kitchen")).ToBeVisibleAsync();
        await Expect(ana.Form().GetByLabel("Delivery time")).ToHaveValueAsync("14:00");
        await Expect(ana.Form().GetByLabel("Delivery room")).ToHaveValueAsync(Seed.ThamesId.ToString());
        await ana.FillFieldAsync("Portions", "6");
        await ana.ConfirmAsync();
        await Expect(ana.Locator("[role=status]")).ToContainTextAsync("Ordered from Main Kitchen");

        await Expect(ana.Cell("Thames", "14:00")).ToHaveAttributeAsync("aria-label", new System.Text.RegularExpressions.Regex("your booking"));
        await ana.OpenTabAsync("Catering");
        await Expect(ana.GetByText("Main Kitchen: 4 of 5 free")).ToBeVisibleAsync();

        await ana.GetByRole(AriaRole.Link, new() { Name = "My bookings" }).ClickAsync();
        var upcoming = ana.GetByRole(AriaRole.Table);
        await Expect(upcoming.GetByRole(AriaRole.Row).Filter(new() { HasText = "Thames" }).Filter(new() { HasText = "14:00" })).ToHaveCountAsync(1);
        await Expect(upcoming.GetByRole(AriaRole.Row).Filter(new() { HasText = "Main Kitchen" })).ToHaveCountAsync(1);

        var ben = await app.SignInAsync("Ben");
        await ben.GoToTomorrowAsync();
        await Expect(ben.Cell("Thames", "14:00")).ToHaveAttributeAsync("aria-label", "Thames, 14:00, booked");
        await ben.OpenTabAsync("Catering");
        await Expect(ben.GetByText("Main Kitchen: 4 of 5 free")).ToBeVisibleAsync();
        await ben.OpenTabAsync("Rooms");
        await ben.Cell("Thames", "16:00").ClickAsync();
        await ben.FillFieldAsync("Start", "14:30");
        await ben.FillFieldAsync("End", "15:30");
        await ben.ConfirmAsync();

        await Expect(ben.Form().Locator("[role=alert]")).ToContainTextAsync("Thames is already booked then");
        await ben.Form().Button("Find alternatives").ClickAsync();
        await Expect(ben.Form().Button("Use Severn")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task A_slow_day_never_replaces_the_day_picked_after_it()
    {
        var ana = await app.SignInAsync("Ana");
        var tomorrow = Pages.LondonToday().AddDays(1).ToString("yyyy-MM-dd");
        await ana.RouteAsync($"**/api/availability?date={tomorrow}*", async route =>
        {
            await Task.Delay(1500);
            await route.ContinueAsync();
        });

        await ana.Button("Next day").ClickAsync();
        await ana.Button("Next day").ClickAsync();
        await ana.WaitForTimeoutAsync(2500);

        // Two days ahead, Ben has Thames from 09:00; tomorrow it is free.
        await Expect(ana.Cell("Thames", "09:00")).ToHaveAttributeAsync("aria-label", "Thames, 09:00, booked");
    }

    [Theory]
    [InlineData("Pacific/Kiritimati")]
    [InlineData("Pacific/Honolulu")]
    public async Task The_overview_shows_London_days_whatever_zone_the_browser_is_in(string browserZone)
    {
        // Kiritimati is 14 hours ahead of London and Honolulu 10 behind, so at any moment one of them is on another date.
        var page = await app.SignInAsync("Ana", browserZone);

        await Expect(page.Locator(".day-label")).ToContainTextAsync(Pages.LondonToday().ToString("dddd d MMMM yyyy"));
    }

    [Fact]
    public async Task A_car_cell_before_a_booking_pre_fills_an_end_that_leaves_the_turnaround_gap()
    {
        var ana = await app.SignInAsync("Ana");
        await ana.GoToTomorrowAsync();
        await ana.OpenTabAsync("Cars");

        // Ben has Pool Car 1 from 09:00 and the car needs a 60-minute gap, so a booking from 07:00 ends by 08:00,
        // and no booking can start at 08:00 or 08:30.
        await Expect(ana.Cell("Pool Car 1", "08:00")).ToHaveAttributeAsync("aria-label", "Pool Car 1, 08:00, turnaround gap");
        await Expect(ana.Cell("Pool Car 1", "08:30")).ToHaveAttributeAsync("aria-label", "Pool Car 1, 08:30, turnaround gap");
        await ana.Cell("Pool Car 1", "07:00").ClickAsync();

        await Expect(ana.Form().GetByLabel("End", new() { Exact = true })).ToHaveValueAsync("08:00");
        await ana.FillFieldAsync("Attendees", "2");
        await ana.ConfirmAsync();
        await Expect(ana.Locator("[role=status]")).ToContainTextAsync("Booked Pool Car 1");
    }

    [Fact]
    public async Task Moving_a_new_catering_order_to_an_earlier_day_keeps_the_form_working()
    {
        var ana = await app.SignInAsync("Ana");
        await ana.GoToTomorrowAsync();
        await ana.GoToTomorrowAsync();
        await Expect(ana.Locator(".day-label")).ToContainTextAsync(Pages.LondonToday().AddDays(2).ToString("dddd d MMMM yyyy"));
        await ana.OpenTabAsync("Catering");
        await ana.Button("Order").ClickAsync();

        await ana.FillFieldAsync("Date", Pages.LondonToday().AddDays(1).ToString("yyyy-MM-dd"));
        await Expect(ana.Locator("#blazor-error-ui")).ToBeHiddenAsync();
        await ana.FillFieldAsync("Portions", "4");
        await ana.Form().GetByLabel("Delivery room").SelectOptionAsync(new SelectOptionValue { Label = "Thames" });
        await ana.ConfirmAsync();

        await Expect(ana.Locator("[role=status]")).ToContainTextAsync("Ordered from Main Kitchen");
    }
}
