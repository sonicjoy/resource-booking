using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace ResourceBooking.E2E;

/// <summary>CUJ 2: each refused booking says why, and nothing is stored.</summary>
[Collection(E2ECollection.Name)]
public class Cuj2RuleRefusalTests(AppFixture app) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await app.ReseedAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Too_many_people_past_closing_and_inside_the_cars_gap_are_each_refused_with_the_reason()
    {
        var ana = await app.SignInAsync("Ana");
        await ana.GoToTomorrowAsync();

        await ana.Cell("Thames", "10:00").ClickAsync();
        await ana.FillAsync("Attendees", "12");
        await ana.ConfirmAsync();
        await Expect(ana.Form().GetByRole(AriaRole.Alert)).ToContainTextAsync("Thames seats 8");

        await ana.FillAsync("Attendees", "6");
        await ana.FillAsync("Start", "15:00");
        await ana.FillAsync("End", "18:30");
        await ana.ConfirmAsync();
        await Expect(ana.Form().GetByRole(AriaRole.Alert)).ToContainTextAsync("Thames closes at 18:00");
        await ana.Form().GetByRole(AriaRole.Button, new() { Name = "Cancel" }).ClickAsync();

        await ana.OpenTabAsync("Cars");
        await Expect(ana.Cell("Pool Car 1", "11:30")).ToHaveAccessibleNameAsync("Pool Car 1, 11:30, turnaround gap");
        await ana.Cell("Pool Car 1", "14:00").ClickAsync();
        await ana.FillAsync("Attendees", "2");
        await ana.FillAsync("Start", "11:30");
        await ana.FillAsync("End", "13:00");
        await ana.ConfirmAsync();
        await Expect(ana.Form().GetByRole(AriaRole.Alert))
            .ToContainTextAsync("Pool Car 1 is booked then, or within its 60-minute gap between bookings");
        await ana.Form().GetByRole(AriaRole.Button, new() { Name = "Cancel" }).ClickAsync();

        await ana.GetByRole(AriaRole.Link, new() { Name = "My bookings" }).ClickAsync();
        var upcoming = ana.GetByRole(AriaRole.Table);
        await Expect(upcoming.GetByRole(AriaRole.Row).Filter(new() { HasText = "Severn" })).ToHaveCountAsync(1);
        await Expect(upcoming.GetByRole(AriaRole.Row).Filter(new() { HasText = "Thames" })).ToHaveCountAsync(0);
        await Expect(upcoming.GetByRole(AriaRole.Row).Filter(new() { HasText = "Pool Car 1" })).ToHaveCountAsync(0);
    }
}
