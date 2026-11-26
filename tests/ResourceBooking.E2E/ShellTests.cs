using Microsoft.Playwright;

namespace ResourceBooking.E2E;

[Collection(E2ECollection.Name)]
public class ShellTests(AppFixture app) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await app.ReseedAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task An_employee_signs_in_lands_on_Book_and_sees_only_employee_navigation()
    {
        var page = await app.SignInAsync("Ana");

        await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "Book" })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "My bookings" })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "Users" })).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByText("London time").First).ToBeVisibleAsync();
    }
}
