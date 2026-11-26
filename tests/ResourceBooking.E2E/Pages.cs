using Microsoft.Playwright;

namespace ResourceBooking.E2E;

/// <summary>What the journeys do on the Book page and the booking form, by the names a screen reader hears.</summary>
public static class Pages
{
    public static ILocator Cell(this IPage page, string resource, string time) =>
        page.GetByRole(AriaRole.Button, new() { NameRegex = new System.Text.RegularExpressions.Regex($"^{resource}, {time}, ") });

    public static async Task GoToTomorrowAsync(this IPage page) =>
        await page.GetByRole(AriaRole.Button, new() { Name = "Next day" }).ClickAsync();

    public static async Task OpenTabAsync(this IPage page, string tab) =>
        await page.GetByRole(AriaRole.Tab, new() { Name = tab }).ClickAsync();

    public static ILocator Form(this IPage page) => page.GetByRole(AriaRole.Dialog);

    /// <summary>Fills a field in the booking form; Fluent inputs keep the real input in their shadow root.</summary>
    public static async Task FillAsync(this IPage page, string label, string value)
    {
        var field = page.Form().GetByLabel(label, new() { Exact = true });
        var inner = field.Locator("input");
        await (await inner.CountAsync() > 0 ? inner : field).FillAsync(value);
    }

    public static async Task ConfirmAsync(this IPage page) =>
        await page.Form().GetByRole(AriaRole.Button, new() { Name = "Confirm" }).ClickAsync();
}
