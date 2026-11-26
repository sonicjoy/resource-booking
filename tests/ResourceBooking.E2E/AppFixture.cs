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

    /// <summary>A fresh browser context signed in as the given seeded user through the sign-in page, landed on Book.</summary>
    public async Task<IPage> SignInAsync(string displayName, string browserZone = "Europe/London")
    {
        var context = await Browser.NewContextAsync(new() { BaseURL = BaseUrl, Locale = "en-GB", TimezoneId = browserZone });
        var page = await context.NewPageAsync();
        await page.GotoAsync("/sign-in");
        var user = page.GetByRole(AriaRole.Button, new() { NameRegex = new System.Text.RegularExpressions.Regex($"^{displayName} ") });
        await WaitForSignInListAsync(page, user);
        await user.ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "Book" }).WaitForAsync();
        return page;
    }

    /// <summary>
    /// Waits for the sign-in list, which appears only once the WebAssembly app has downloaded and asked the server for
    /// it. A fresh browser context downloads the app again, and twice in CI that took over 30 s, so this allows longer,
    /// reloads once, and if the list still isn't there fails with what the page did show.
    /// </summary>
    private static async Task WaitForSignInListAsync(IPage page, ILocator user)
    {
        try
        {
            await user.WaitForAsync(new() { Timeout = 45_000 });
            return;
        }
        catch (TimeoutException)
        {
            await page.ReloadAsync();
        }
        try
        {
            await user.WaitForAsync(new() { Timeout = 45_000 });
        }
        catch (TimeoutException e)
        {
            var shown = await page.Locator("body").InnerTextAsync();
            throw new TimeoutException($"The sign-in list never appeared, even after a reload. The page showed: {shown[..Math.Min(shown.Length, 500)]}", e);
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
