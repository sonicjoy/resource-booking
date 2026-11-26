using System.Net.Http.Json;
using Microsoft.Playwright;
using ResourceBooking.Data;
using static Microsoft.Playwright.Assertions;

namespace ResourceBooking.E2E;

/// <summary>Each refusal the admin API can give shows its reason on the admin screens (plan step 9: every error case).</summary>
[Collection(E2ECollection.Name)]
public class AdminRefusalTests(AppFixture app) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await app.ReseedAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Day(int fromToday) => Pages.LondonToday().AddDays(fromToday).ToString("yyyy-MM-dd");

    [Fact]
    public async Task An_email_in_use_is_refused_in_the_form()
    {
        var sam = await app.SignInAsync("Sam");
        await sam.GoToAsync("Users");
        await sam.Button("Add user").ClickAsync();
        var form = sam.Modal("Add user");
        await form.FillInAsync("Email", "ANA@sonicjoy-solutions.com");
        await form.FillInAsync("Display name", "Ana again");
        await form.Button("Save").ClickAsync();

        await Expect(form.Locator("[role=alert]")).ToContainTextAsync("ana@sonicjoy-solutions.com is already in use");
    }

    [Fact]
    public async Task The_last_admin_deactivating_themselves_is_told_why_not()
    {
        var sam = await app.SignInAsync("Sam");
        await sam.GoToAsync("Users");
        await sam.Row("sam@sonicjoy-solutions.com").Button("Deactivate").ClickAsync();
        await sam.Modal("Deactivate Sam").Button("Deactivate").ClickAsync();

        await Expect(sam.Locator("[role=alert]")).ToContainTextAsync("There must be at least one active admin");
        await Expect(sam.Row("sam@sonicjoy-solutions.com")).ToContainTextAsync("Active");
    }

    [Fact]
    public async Task A_last_day_in_the_past_or_across_a_booking_is_refused_with_the_reason()
    {
        using var ana = await app.ApiAsAsync(Seed.AnaId);
        (await ana.PostAsJsonAsync("/api/bookings",
            new { resourceId = Seed.PoolCar1Id, start = $"{Day(2)}T12:00", end = $"{Day(3)}T12:00", attendees = 2 }, Ct)).EnsureSuccessStatusCode();
        var sam = await app.SignInAsync("Sam");
        await sam.GoToAsync("Resources");

        await sam.Row("Pool Car 1").Button("Retire").ClickAsync();
        var retire = sam.Modal("Retire Pool Car 1");
        await retire.FillInAsync("Last day", Day(-1));
        await retire.Button("Retire").ClickAsync();
        await Expect(retire.Locator("[role=alert]")).ToContainTextAsync("The last day can't be before today");

        await retire.FillInAsync("Last day", Day(2));
        await retire.Button("Retire").ClickAsync();
        await Expect(retire.Locator("[role=alert]")).ToContainTextAsync("Pool Car 1 has a booking running past");
    }

    [Fact]
    public async Task Lowering_a_kitchens_orders_below_one_in_use_is_refused()
    {
        using var ana = await app.ApiAsAsync(Seed.AnaId);
        for (var i = 0; i < 5; i++)
        {
            (await ana.PostAsJsonAsync("/api/bookings",
                new { resourceId = Seed.MainKitchenId, deliveryAt = $"{Day(1)}T12:30", attendees = 4, deliveryRoomId = Seed.ThamesId }, Ct)).EnsureSuccessStatusCode();
        }
        var sam = await app.SignInAsync("Sam");
        await sam.GoToAsync("Resources");

        await sam.Row("Main Kitchen").Button("Edit").ClickAsync();
        var form = sam.Modal("Edit Main Kitchen");
        await form.FillInAsync("Orders a day", "4");
        await form.Button("Save").ClickAsync();

        await Expect(form.Locator("[role=alert]")).ToContainTextAsync("Main Kitchen has future orders on unit 5");
    }

    [Fact]
    public async Task A_bad_rule_value_is_shown_next_to_its_field()
    {
        var sam = await app.SignInAsync("Sam");
        await sam.GoToAsync("Resources");
        await sam.Button("Add resource").ClickAsync();
        var form = sam.Modal("Add resource");
        await form.FillInAsync("Type", "Car");
        await form.FillInAsync("Name", "Pool Car 3");
        await form.FillInAsync("Seats", "4");
        await form.FillInAsync("Gap between bookings (minutes)", "1500");
        await form.FillInAsync("Shortest booking (minutes)", "120");
        await form.FillInAsync("Longest booking (hours)", "1");
        await form.Button("Save").ClickAsync();

        await Expect(form.Locator(".field-error").Filter(new() { HasText = "The gap between bookings must be more than zero and at most 1 day." })).ToBeVisibleAsync();
        await Expect(form.Locator(".field-error").Filter(new() { HasText = "longest booking" })).ToBeVisibleAsync();
    }

    [Fact]
    public async Task A_field_hidden_by_changing_the_type_is_not_sent()
    {
        var sam = await app.SignInAsync("Sam");
        await sam.GoToAsync("Resources");
        await sam.Button("Add resource").ClickAsync();
        var form = sam.Modal("Add resource");
        await form.FillInAsync("Type", "Catering");
        await form.FillInAsync("Orders a day", "51");
        await form.FillInAsync("Type", "Car");
        await form.FillInAsync("Name", "Pool Car 3");
        await form.FillInAsync("Seats", "4");
        await form.FillInAsync("Longest booking (hours)", "1.5");
        await form.Button("Save").ClickAsync();

        await Expect(sam.Row("Pool Car 3")).ToContainTextAsync("at most 1.5 h");
    }

    [Fact]
    public async Task The_last_admin_demoting_themselves_is_told_why_not()
    {
        var sam = await app.SignInAsync("Sam");
        await sam.GoToAsync("Users");
        await sam.Row("sam@sonicjoy-solutions.com").Button("Edit").ClickAsync();
        var form = sam.Modal("Edit Sam");
        await form.FillInAsync("Role", "Employee");
        await form.Button("Save").ClickAsync();

        await Expect(form.Locator("[role=alert]")).ToContainTextAsync("There must be at least one active admin");
    }

    [Fact]
    public async Task Saving_a_resource_unchanged_keeps_its_hours_and_rules()
    {
        var sam = await app.SignInAsync("Sam");
        await sam.GoToAsync("Resources");
        var before = await sam.Row("Main Kitchen").InnerTextAsync();
        var carBefore = await sam.Row("Pool Car 1").InnerTextAsync();

        await sam.Row("Main Kitchen").Button("Edit").ClickAsync();
        await sam.Modal("Edit Main Kitchen").Button("Save").ClickAsync();
        await Expect(sam.Locator("[role=status]")).ToContainTextAsync("Saved Main Kitchen");
        await sam.Row("Pool Car 1").Button("Edit").ClickAsync();
        await sam.Modal("Edit Pool Car 1").Button("Save").ClickAsync();
        await Expect(sam.Locator("[role=status]")).ToContainTextAsync("Saved Pool Car 1");

        Assert.Equal(before, await sam.Row("Main Kitchen").InnerTextAsync());
        Assert.Equal(carBefore, await sam.Row("Pool Car 1").InnerTextAsync());
    }

    [Fact]
    public async Task A_notice_under_a_quarter_hour_can_be_saved()
    {
        var sam = await app.SignInAsync("Sam");
        await sam.GoToAsync("Resources");
        await sam.Row("Thames").Button("Edit").ClickAsync();
        var form = sam.Modal("Edit Thames");
        await form.FillInAsync("Notice needed (hours)", "0.1");
        await form.Button("Save").ClickAsync();

        await Expect(sam.Row("Thames")).ToContainTextAsync("0.1 h notice");
    }
}
