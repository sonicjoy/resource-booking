using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using ResourceBooking.Data;
using static Microsoft.Playwright.Assertions;

namespace ResourceBooking.E2E;

/// <summary>
/// Changing and cancelling from My bookings, with the browser in Los Angeles time: every time on screen
/// is London time whatever zone the browser is in.
/// </summary>
[Collection(E2ECollection.Name)]
public class MyBookingsTests(AppFixture app) : IAsyncLifetime
{
    private const string Browser = "America/Los_Angeles";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await app.ReseedAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static string Day(int fromToday) => Pages.LondonToday().AddDays(fromToday).ToString("yyyy-MM-dd");

    [Fact]
    public async Task Saving_a_multi_day_car_booking_unchanged_keeps_its_end_day()
    {
        using var api = await app.ApiAsAsync(Seed.AnaId);
        (await api.PostAsJsonAsync("/api/bookings",
            new { resourceId = Seed.PoolCar1Id, start = $"{Day(4)}T09:00", end = $"{Day(5)}T17:00", attendees = 2, title = "Road trip" }, Ct)).EnsureSuccessStatusCode();
        var ana = await app.SignInAsync("Ana", Browser);
        await ana.GetByRole(AriaRole.Link, new() { Name = "My bookings" }).ClickAsync();
        var row = ana.GetByRole(AriaRole.Row).Filter(new() { HasText = "Road trip" });
        await Expect(row).ToContainTextAsync("09:00");

        await row.Locator("fluent-button, button").Filter(new() { HasTextRegex = new Regex("^\\s*Edit\\s*$") }).ClickAsync();
        await Expect(ana.Form().GetByLabel("End date")).ToHaveValueAsync(Day(5));
        await ana.ConfirmAsync();

        await Expect(ana.Locator("[role=status]")).ToContainTextAsync("Changed your booking of Pool Car 1");
        var bookings = await api.GetFromJsonAsync<List<BookingView>>("/api/bookings/mine", Ct);
        var trip = bookings!.Single(b => b.Title == "Road trip");
        Assert.Equal((DateTime.Parse($"{Day(4)}T09:00"), DateTime.Parse($"{Day(5)}T17:00")), (trip.Start, trip.End));
    }

    [Fact]
    public async Task Moving_a_booking_to_a_later_time_shows_the_new_time_in_my_bookings_and_on_Book()
    {
        var ana = await app.SignInAsync("Ana", Browser);
        await ana.GetByRole(AriaRole.Link, new() { Name = "My bookings" }).ClickAsync();
        var row = ana.GetByRole(AriaRole.Row).Filter(new() { HasText = "Quarterly review" });
        await Expect(row).ToContainTextAsync("10:00 to 11:00");

        await row.Locator("fluent-button, button").Filter(new() { HasTextRegex = new Regex("^\\s*Edit\\s*$") }).ClickAsync();
        await ana.FillFieldAsync("Start", "14:00");
        await ana.FillFieldAsync("End", "15:30");
        await ana.ConfirmAsync();

        await Expect(ana.Locator("[role=status]")).ToContainTextAsync("Changed your booking of Severn");
        await Expect(row).ToHaveCountAsync(1);
        await Expect(row).ToContainTextAsync("14:00 to 15:30");

        await ana.GotoAsync($"/book?date={Day(3)}&type=MeetingRoom");
        await Expect(ana.Cell("Severn", "14:00")).ToHaveAttributeAsync("aria-label", "Severn, 14:00, your booking: Quarterly review");
        await Expect(ana.Cell("Severn", "15:00")).ToHaveAttributeAsync("aria-label", "Severn, 15:00, your booking: Quarterly review");
        await Expect(ana.Cell("Severn", "10:00")).ToHaveAttributeAsync("aria-label", "Severn, 10:00, free");
    }

    [Fact]
    public async Task An_edit_that_overlaps_says_so_without_offering_another_resource()
    {
        using var ben = await app.ApiAsAsync(Seed.BenId);
        (await ben.PostAsJsonAsync("/api/bookings",
            new { resourceId = Seed.SevernId, start = $"{Day(3)}T14:00", end = $"{Day(3)}T15:00", attendees = 2 }, Ct)).EnsureSuccessStatusCode();
        var ana = await app.SignInAsync("Ana", Browser);
        await ana.GetByRole(AriaRole.Link, new() { Name = "My bookings" }).ClickAsync();
        var row = ana.GetByRole(AriaRole.Row).Filter(new() { HasText = "Quarterly review" });

        await row.Locator("fluent-button, button").Filter(new() { HasTextRegex = new Regex("^\\s*Edit\\s*$") }).ClickAsync();
        await ana.FillFieldAsync("Start", "14:00");
        await ana.FillFieldAsync("End", "15:00");
        await ana.ConfirmAsync();

        await Expect(ana.Form().Locator("[role=alert]")).ToContainTextAsync("Severn is already booked then");
        await Expect(ana.Form().Button("Find alternatives")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task Cancelling_asks_first_then_moves_the_booking_out_of_upcoming()
    {
        var ana = await app.SignInAsync("Ana", Browser);
        await ana.GetByRole(AriaRole.Link, new() { Name = "My bookings" }).ClickAsync();
        var row = ana.GetByRole(AriaRole.Row).Filter(new() { HasText = "Quarterly review" });
        await Expect(row).ToContainTextAsync("10:00 to 11:00");

        await row.Locator("fluent-button, button").Filter(new() { HasTextRegex = new Regex("^\\s*Cancel\\s*$") }).ClickAsync();
        await ana.Locator("[role=dialog]").Button("Cancel booking").ClickAsync();

        await Expect(ana.Locator("[role=status]")).ToContainTextAsync("Cancelled your booking of Severn");
        await Expect(row).ToContainTextAsync("Cancelled");
        await Expect(row.Locator("fluent-button, button")).ToHaveCountAsync(0);
    }

    private sealed record BookingView(string? Title, DateTime Start, DateTime End);
}
