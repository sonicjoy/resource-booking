using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace ResourceBooking.E2E;

/// <summary>What the journeys do on the Book page and the booking form, by the names a screen reader hears.</summary>
public static class Pages
{
    /// <summary>
    /// A button by its exact text. Fluent buttons are web components whose button role comes from
    /// ElementInternals, which screen readers read but Playwright's role engine doesn't, so they are found by tag.
    /// </summary>
    public static ILocator Button(this ILocator scope, string name) =>
        scope.Locator("fluent-button, button").Filter(new() { HasTextRegex = new Regex($"^\\s*{Regex.Escape(name)}\\s*$") });

    public static ILocator Button(this IPage page, string name) => page.Locator("body").Button(name);

    public static ILocator Cell(this IPage page, string resource, string time) =>
        page.Locator($"button.cell[aria-label^='{resource}, {time}, ']");

    public static async Task GoToTomorrowAsync(this IPage page) => await page.Button("Next day").ClickAsync();

    public static async Task OpenTabAsync(this IPage page, string tab) =>
        await page.GetByRole(AriaRole.Tab, new() { Name = tab, Exact = true }).ClickAsync();

    public static ILocator Form(this IPage page) => page.Locator("[role=dialog][aria-labelledby='booking-form-title']");

    /// <summary>
    /// Fills a field in the booking form by its label. A Fluent input is a web component with the real
    /// input in its shadow root, so the matched custom element is filled through that input.
    /// </summary>
    public static async Task FillFieldAsync(this IPage page, string label, string value)
    {
        var matches = page.Form().GetByLabel(label, new() { Exact = true });
        await matches.First.WaitForAsync();
        for (var i = 0; i < await matches.CountAsync(); i++)
        {
            var match = matches.Nth(i);
            var tag = await match.EvaluateAsync<string>("e => e.tagName.toLowerCase()");
            if (tag is "input" or "select" or "textarea")
            {
                await match.FillAsync(value);
                return;
            }
            if (tag.StartsWith("fluent-", StringComparison.Ordinal))
            {
                await match.Locator("input").FillAsync(value);
                return;
            }
        }
        throw new InvalidOperationException($"No fillable field labelled {label}");
    }

    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    /// <summary>Today in the company's zone, which the app's labels and "Next day" count from.</summary>
    public static DateOnly LondonToday() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, London).DateTime);

    /// <summary>A dialog by its heading.</summary>
    public static ILocator Modal(this IPage page, string heading) =>
        page.Locator("[role=dialog]").Filter(new() { Has = page.GetByRole(AriaRole.Heading, new() { Name = heading }) });

    /// <summary>Fills a labelled field inside any scope (a dialog, a filter bar).</summary>
    public static async Task FillInAsync(this ILocator scope, string label, string value)
    {
        var matches = scope.GetByLabel(label, new() { Exact = true });
        await matches.First.WaitForAsync();
        for (var i = 0; i < await matches.CountAsync(); i++)
        {
            var match = matches.Nth(i);
            var tag = await match.EvaluateAsync<string>("e => e.tagName.toLowerCase()");
            if (tag == "select")
            {
                await match.SelectOptionAsync(new SelectOptionValue { Label = value });
                return;
            }
            if (tag is "input" or "textarea")
            {
                await match.FillAsync(value);
                return;
            }
            if (tag.StartsWith("fluent-", StringComparison.Ordinal))
            {
                await match.Locator("input").FillAsync(value);
                return;
            }
        }
        throw new InvalidOperationException($"No fillable field labelled {label}");
    }

    /// <summary>A table row containing the given text.</summary>
    public static ILocator Row(this IPage page, string text) => page.GetByRole(AriaRole.Row).Filter(new() { HasText = text });

    public static async Task GoToAsync(this IPage page, string link) =>
        await page.GetByRole(AriaRole.Link, new() { Name = link, Exact = true }).ClickAsync();

    public static async Task ConfirmAsync(this IPage page) => await page.Form().Button("Confirm").ClickAsync();
}
