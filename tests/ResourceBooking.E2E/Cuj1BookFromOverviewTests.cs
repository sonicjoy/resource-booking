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
        await ben.Cell("Thames", "16:00").ClickAsync();
        await ben.FillFieldAsync("Start", "14:30");
        await ben.FillFieldAsync("End", "15:30");
        await ben.ConfirmAsync();

        await Expect(ben.Form().Locator("[role=alert]")).ToContainTextAsync("Thames is already booked then");
        await ben.Form().Button("Find alternatives").ClickAsync();
        await Expect(ben.Form().Button("Use Severn")).ToBeVisibleAsync();
    }
}
