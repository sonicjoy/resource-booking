using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Playwright;
using ResourceBooking.Data;
using Testcontainers.PostgreSql;

namespace ResourceBooking.E2E;

/// <summary>
/// The real app on Kestrel with its own PostgreSQL container and the real clock, plus one Chromium.
/// Set E2E_BASE_URL to run the same journeys against a deployed app instead; nothing is started then.
/// </summary>
public sealed class AppFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _db;
    private Factory? _app;
    private IPlaywright? _playwright;

    public string BaseUrl { get; private set; } = "";
    public IBrowser Browser { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        BaseUrl = Environment.GetEnvironmentVariable("E2E_BASE_URL")?.TrimEnd('/') ?? "";
        if (BaseUrl == "")
        {
            _db = new PostgreSqlBuilder("postgres:17-alpine").Build();
            await _db.StartAsync();
            _app = new Factory(_db.GetConnectionString());
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
        using var http = new HttpClient(new HttpClientHandler { UseCookies = true }) { BaseAddress = new Uri(BaseUrl) };
        http.DefaultRequestHeaders.Add("Origin", BaseUrl);
        (await http.PostAsJsonAsync("/api/auth/sign-in", new { userId = Seed.SamId })).EnsureSuccessStatusCode();
        (await http.PostAsync("/api/admin/reseed", null)).EnsureSuccessStatusCode();
    }

    /// <summary>A fresh browser context signed in as the given seeded user through the sign-in page, landed on Book.</summary>
    public async Task<IPage> SignInAsync(string displayName)
    {
        var context = await Browser.NewContextAsync(new() { BaseURL = BaseUrl, Locale = "en-GB", TimezoneId = "Europe/London" });
        var page = await context.NewPageAsync();
        await page.GotoAsync("/sign-in");
        await page.GetByRole(AriaRole.Button, new() { NameRegex = new System.Text.RegularExpressions.Regex($"^{displayName}\b") }).ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "Book" }).WaitForAsync();
        return page;
    }

    private sealed class Factory(string connectionString) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:Db", connectionString);
            builder.UseSetting("Demo:ReseedWindowSeconds", "0");
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class E2ECollection : ICollectionFixture<AppFixture>
{
    public const string Name = "e2e";
}
