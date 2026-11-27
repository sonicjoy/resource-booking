using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Playwright;
using ResourceBooking.Data;
using ResourceBooking.Tests.Infrastructure;
using Testcontainers.PostgreSql;

namespace ResourceBooking.E2E;

/// <summary>
/// The real app on Kestrel with its own PostgreSQL container and the real clock, plus one Chromium.
/// Set E2E_BASE_URL to run the same journeys against a deployed app instead; nothing is started then.
/// The local app's model is <see cref="Model"/>, a scripted stub.
/// </summary>
public sealed class AppFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _db;
    private Factory? _app;
    private IPlaywright? _playwright;

    public string BaseUrl { get; private set; } = "";

    /// <summary>True when the journeys run against a deployed app, whose model can't be scripted.</summary>
    public bool Remote { get; private set; }

    /// <summary>The local app's model; journeys script it before they talk to the assistant.</summary>
    public StubChatClient Model { get; } = new();
    public IBrowser Browser { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        BaseUrl = Environment.GetEnvironmentVariable("E2E_BASE_URL")?.TrimEnd('/') ?? "";
        Remote = BaseUrl != "";
        if (!Remote)
        {
            _db = new PostgreSqlBuilder("postgres:17-alpine").Build();
            await _db.StartAsync();
            _app = new Factory(_db.GetConnectionString(), Model);
            _app.UseKestrel(0);
            _app.StartServer();
            BaseUrl = _app.ClientOptions.BaseAddress.ToString().TrimEnd('/');
        }
        _playwright = await Playwright.CreateAsync();
        Browser = await _playwright.Chromium.LaunchAsync(new() { Headless = Environment.GetEnvironmentVariable("E2E_HEADED") is null });
    }

    public async ValueTask DisposeAsync()
    {
        await Browser.DisposeAsync();
        _playwright?.Dispose();
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
        if (_db is not null)
        {
            await _db.DisposeAsync();
        }
    }

    /// <summary>Restores the seed, signed in as Sam the admin, so each journey starts from the same data.</summary>
    public async Task ReseedAsync()
    {
        using var http = await ApiAsAsync(Seed.SamId);
        (await http.PostAsync("/api/admin/reseed", null)).EnsureSuccessStatusCode();
    }

    /// <summary>An API client signed in as a seeded user, for setting up a journey's data.</summary>
    public async Task<HttpClient> ApiAsAsync(Guid userId)
    {
        var http = new HttpClient(new HttpClientHandler { UseCookies = true }) { BaseAddress = new Uri(BaseUrl) };
        http.DefaultRequestHeaders.Add("Origin", BaseUrl);
        (await http.PostAsJsonAsync("/api/auth/sign-in", new { userId })).EnsureSuccessStatusCode();
        return http;
    }

    /// <summary>A fresh browser context signed in as the given seeded user through the sign-in page, then on Book.</summary>
    public async Task<IPage> SignInAsync(string displayName, string browserZone = "Europe/London")
    {
        var page = await LandAsync(displayName, browserZone);
        await page.GetByRole(AriaRole.Link, new() { Name = "Book", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "Book" }).WaitForAsync();
        return page;
    }

    /// <summary>A fresh browser context signed in as the given seeded user, left on the page sign-in lands on (Month).</summary>
    public async Task<IPage> LandAsync(string displayName, string browserZone = "Europe/London")
    {
        var context = await Browser.NewContextAsync(new() { BaseURL = BaseUrl, Locale = "en-GB", TimezoneId = browserZone });
        var page = await context.NewPageAsync();
        await page.GotoAsync("/sign-in");
        var user = page.GetByRole(AriaRole.Button, new() { NameRegex = new System.Text.RegularExpressions.Regex($"^{displayName} ") });
        await WaitForSignInListAsync(page, user, displayName);
        await user.ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "Month", Level = 1 }).WaitForAsync();
        return page;
    }

    /// <summary>
    /// Waits for the sign-in list in three steps, so a failure says which one broke. Prerendering is off, so the
    /// "Sign in" heading appears only once the WebAssembly app has started; a fresh browser context downloads it
    /// again, and CI twice timed out here (suspected slow start, not confirmed). That step allows 45 s and reloads
    /// once, writing the reload to the test output so a rescued sign-in leaves a trace. Then the list or the page's
    /// own error, then the named user.
    /// </summary>
    private static async Task WaitForSignInListAsync(IPage page, ILocator user, string displayName)
    {
        var started = page.GetByRole(AriaRole.Heading, new() { Name = "Sign in", Level = 1 });
        try
        {
            await started.WaitForAsync(new() { Timeout = 45_000 });
        }
        catch (TimeoutException)
        {
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"Sign-in page hadn't started after 45 s at {page.Url}; reloading once. The page showed: {await ShownAsync(page)}");
            try
            {
                await page.ReloadAsync();
                await started.WaitForAsync(new() { Timeout = 45_000 });
            }
            catch (TimeoutException e)
            {
                throw new TimeoutException($"The app never started on the sign-in page, even after a reload. The page showed: {await ShownAsync(page)}", e);
            }
        }

        var alert = page.Locator("[role=alert]");
        try
        {
            await page.Locator("ul.people button, [role=alert]").First.WaitForAsync(new() { Timeout = 30_000 });
        }
        catch (TimeoutException e)
        {
            throw new TimeoutException($"The app started but the sign-in list never arrived. The page showed: {await ShownAsync(page)}", e);
        }
        if (await alert.CountAsync() > 0)
        {
            throw new InvalidOperationException($"The sign-in list failed to load: {await alert.First.InnerTextAsync()}");
        }

        try
        {
            await user.WaitForAsync(new() { Timeout = 5_000 });
        }
        catch (TimeoutException e)
        {
            throw new InvalidOperationException($"No sign-in button for {displayName}. The list shows: {await ShownAsync(page)}", e);
        }
    }

    private static async Task<string> ShownAsync(IPage page)
    {
        try
        {
            var text = await page.Locator("body").InnerTextAsync(new() { Timeout = 5_000 });
            return text.Length > 500 ? text[..500] : text;
        }
        catch (PlaywrightException e)
        {
            return $"(couldn't read the page: {e.Message})";
        }
    }

    private sealed class Factory(string connectionString, IChatClient model) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:Db", connectionString);
            builder.UseSetting("Demo:ReseedWindowSeconds", "0");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IChatClient>();
                services.AddSingleton(model);
            });
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class E2ECollection : ICollectionFixture<AppFixture>
{
    public const string Name = "e2e";
}
