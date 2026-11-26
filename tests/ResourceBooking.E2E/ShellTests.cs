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

        await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "Book", Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "My bookings" })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "Users" })).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByText("London time").First).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Signing_out_returns_to_sign_in_and_the_session_is_gone()
    {
        var page = await app.SignInAsync("Ana");

        await page.Button("Sign out").ClickAsync();

        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Sign in" })).ToBeVisibleAsync();
        await page.GotoAsync("/my-bookings");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Sign in" })).ToBeVisibleAsync();
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/my-bookings")]
    public async Task A_signed_out_visitor_is_sent_to_sign_in(string path)
    {
        var context = await app.Browser.NewContextAsync(new() { BaseURL = app.BaseUrl });
        var page = await context.NewPageAsync();

        await page.GotoAsync(path);

        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Sign in" })).ToBeVisibleAsync();
    }
}
