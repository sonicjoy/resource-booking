using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace ResourceBooking.E2E;

/// <summary>CUJ 5: utilisation for last week from the seed's past bookings, then History.</summary>
[Collection(E2ECollection.Name)]
public class Cuj5UtilisationAndHistoryTests(AppFixture app) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await app.ReseedAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Sam_sees_last_weeks_utilisation_and_who_did_what()
    {
        var sam = await app.SignInAsync("Sam");
        await sam.GoToAsync("Utilisation");
        // The page opens on the seven days ending yesterday: last week.
        await Expect(sam.Locator(".filters").GetByLabel("From")).ToHaveValueAsync(Pages.LondonToday().AddDays(-7).ToString("yyyy-MM-dd"));

        // Seeded last week: Thames 6 of 70 open hours; Pool Car 1 33 of 84; Main Kitchen 1 of 35 unit-days.
        await Expect(sam.Row("Thames")).ToContainTextAsync("8.6%");
        await Expect(sam.Row("Pool Car 1")).ToContainTextAsync("39.3%");
        await Expect(sam.Row("Main Kitchen")).ToContainTextAsync("1 of 35 unit-days");

        await sam.GoToAsync("History");
        await Expect(sam.Row("Design review").Or(sam.Row("Thames,")).First).ToBeVisibleAsync();
        await Expect(sam.Row("booked").First).ToContainTextAsync("System");
    }

    [Fact]
    public async Task A_slow_unfiltered_history_never_replaces_the_filter_picked_after_it()
    {
        var sam = await app.SignInAsync("Sam");
        await sam.RouteAsync(new System.Text.RegularExpressions.Regex("/api/history$"), async route =>
        {
            await Task.Delay(1500);
            await route.ContinueAsync();
        });

        await sam.GoToAsync("History");
        await sam.Locator(".filters").FillInAsync("Entity", "Bookings");
        await sam.WaitForTimeoutAsync(2500);

        await Expect(sam.Row("User:")).ToHaveCountAsync(0);
        await Expect(sam.Row("Booking:").First).ToBeVisibleAsync();
    }
}
