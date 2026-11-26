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
        await ana.FillAsync("Attendees", "6");
        await ana.ConfirmAsync();

        await Expect(ana.GetByRole(AriaRole.Status)).ToContainTextAsync("Booked Thames");
        await ana.GetByRole(AriaRole.Button, new() { Name = "Add catering" }).ClickAsync();
        await Expect(ana.Form().GetByText("Main Kitchen")).ToBeVisibleAsync();
        await ana.FillAsync("Portions", "6");
        await ana.ConfirmAsync();
        await Expect(ana.GetByRole(AriaRole.Status)).ToContainTextAsync("Ordered from Main Kitchen");

        await Expect(ana.Cell("Thames", "14:00")).ToHaveAccessibleNameAsync(new System.Text.RegularExpressions.Regex("your booking"));
        await ana.OpenTabAsync("Catering");
        await Expect(ana.GetByText("Main Kitchen: 4 of 5 free")).ToBeVisibleAsync();

        await ana.GetByRole(AriaRole.Link, new() { Name = "My bookings" }).ClickAsync();
        var upcoming = ana.GetByRole(AriaRole.Table);
        await Expect(upcoming.GetByRole(AriaRole.Row).Filter(new() { HasText = "Thames" }).Filter(new() { HasText = "14:00" })).ToHaveCountAsync(1);
        await Expect(upcoming.GetByRole(AriaRole.Row).Filter(new() { HasText = "Main Kitchen" })).ToHaveCountAsync(1);

        var ben = await app.SignInAsync("Ben");
        await ben.GoToTomorrowAsync();
        await Expect(ben.Cell("Thames", "14:00")).ToHaveAccessibleNameAsync("Thames, 14:00, booked");
        await ben.Cell("Thames", "16:00").ClickAsync();
        await ben.FillAsync("Start", "14:30");
        await ben.FillAsync("End", "15:30");
        await ben.ConfirmAsync();

        await Expect(ben.Form().GetByRole(AriaRole.Alert)).ToContainTextAsync("Thames is already booked then");
        await ben.Form().GetByRole(AriaRole.Button, new() { Name = "Find alternatives" }).ClickAsync();
        await Expect(ben.Form().GetByRole(AriaRole.Button, new() { Name = "Use Severn" })).ToBeVisibleAsync();
    }
}
