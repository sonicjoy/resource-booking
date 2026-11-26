using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace ResourceBooking.E2E;

/// <summary>CUJ 4: retiring a room and deactivating a leaver cancel their future bookings, tell who should be told, and show in History.</summary>
[Collection(E2ECollection.Name)]
public class Cuj4RetireAndDeactivateTests(AppFixture app) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await app.ReseedAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Sam_retires_Severn_and_deactivates_Ben()
    {
        var ben = await app.SignInAsync("Ben");
        var sam = await app.SignInAsync("Sam");

        await sam.GoToAsync("Resources");
        await sam.Row("Severn").Locator("fluent-button, button").Filter(new() { HasText = "Retire" }).ClickAsync();
        var retire = sam.Modal("Retire Severn");
        await Expect(retire).ToContainTextAsync("future bookings after it will be cancelled and their owners told");
        await retire.FillInAsync("Last day", Pages.LondonToday().AddDays(1).ToString("yyyy-MM-dd"));
        await retire.Button("Retire").ClickAsync();
        await Expect(sam.Locator("[role=status]")).ToContainTextAsync("Severn retired; 1 booking cancelled");

        await sam.GoToAsync("Users");
        await sam.Row("ben@sonicjoy-solutions.com").Locator("fluent-button, button").Filter(new() { HasText = "Deactivate" }).ClickAsync();
        await sam.Modal("Deactivate Ben").Button("Deactivate").ClickAsync();
        await Expect(sam.Locator("[role=status]")).ToContainTextAsync("Ben deactivated; 2 bookings cancelled");
        await Expect(sam.Row("ben@sonicjoy-solutions.com")).ToContainTextAsync("Inactive");

        await ben.GoToAsync("My bookings");
        await Expect(ben.GetByRole(AriaRole.Heading, new() { Name = "Sign in" })).ToBeVisibleAsync();

        var ana = await app.SignInAsync("Ana");
        await ana.Button("Notices (1 unread)").ClickAsync();
        await Expect(ana.Locator("[role=dialog]").Filter(new() { HasText = "Notices" })).ToContainTextAsync("Severn");
        await ana.GoToTomorrowAsync();
        await ana.GoToTomorrowAsync();
        await Expect(ana.Locator(".grid")).Not.ToContainTextAsync("Severn");

        await sam.GoToAsync("History");
        await sam.Locator(".filters").FillInAsync("Entity", "Bookings");
        await Expect(sam.Row("cancelled").Filter(new() { HasText = "Sam" })).ToHaveCountAsync(3);
        await sam.GoToAsync("Utilisation");
        await Expect(sam.Row("Severn")).ToBeVisibleAsync();
    }
}
