using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace ResourceBooking.E2E;

/// <summary>CUJ 3: an admin adds a user and a car; the new user's long booking is refused; History names the admin.</summary>
[Collection(E2ECollection.Name)]
public class Cuj3AdminSetupTests(AppFixture app) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await app.ReseedAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Sam_adds_Cara_and_Pool_Car_2_and_Caras_four_day_booking_is_refused()
    {
        var sam = await app.SignInAsync("Sam");
        await sam.GoToAsync("Users");
        await sam.Button("Add user").ClickAsync();
        var addUser = sam.Modal("Add user");
        await addUser.FillInAsync("Email", "cara@sonicjoy-solutions.com");
        await addUser.FillInAsync("Display name", "Cara");
        await addUser.FillInAsync("Role", "Employee");
        await addUser.Button("Save").ClickAsync();
        await Expect(sam.Row("cara@sonicjoy-solutions.com")).ToContainTextAsync("Employee");

        await sam.GoToAsync("Resources");
        await sam.Button("Add resource").ClickAsync();
        var addResource = sam.Modal("Add resource");
        await addResource.FillInAsync("Type", "Car");
        await addResource.FillInAsync("Name", "Pool Car 2");
        await addResource.FillInAsync("Seats", "4");
        await addResource.FillInAsync("Opens at", "07:00");
        await addResource.FillInAsync("Closes at", "19:00");
        await addResource.FillInAsync("Longest booking (hours)", "72");
        await addResource.FillInAsync("Gap between bookings (minutes)", "60");
        await Expect(addResource.GetByLabel("Orders a day")).ToHaveCountAsync(0);
        await addResource.Button("Save").ClickAsync();
        await Expect(sam.Row("Pool Car 2")).ToContainTextAsync("Car");

        var cara = await app.SignInAsync("Cara");
        await cara.GoToTomorrowAsync();
        await cara.OpenTabAsync("Cars");
        await cara.Cell("Pool Car 2", "09:00").ClickAsync();
        await cara.FillFieldAsync("End date", Pages.LondonToday().AddDays(5).ToString("yyyy-MM-dd"));
        await cara.FillFieldAsync("Attendees", "2");
        await cara.ConfirmAsync();
        await Expect(cara.Form().Locator("[role=alert]")).ToContainTextAsync("Pool Car 2 can be booked for 3 days at most");

        await sam.GoToAsync("History");
        await Expect(sam.Row("Cara").Filter(new() { HasText = "added" })).ToContainTextAsync("Sam");
        await Expect(sam.Row("Pool Car 2").Filter(new() { HasText = "added" })).ToContainTextAsync("Sam");
    }

    [Fact]
    public async Task Employees_see_no_admin_pages()
    {
        var ana = await app.SignInAsync("Ana");

        await Expect(ana.GetByRole(AriaRole.Link, new() { Name = "Users", Exact = true })).ToHaveCountAsync(0);
        await ana.GotoAsync("/admin/users");
        await Expect(ana.GetByRole(AriaRole.Heading, new() { Name = "Book" })).ToBeVisibleAsync();
    }
}
