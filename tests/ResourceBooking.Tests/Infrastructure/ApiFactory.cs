using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using ResourceBooking.Data;

namespace ResourceBooking.Tests.Infrastructure;

/// <summary>
/// The real app on its own database with a fake clock. Start with <see cref="CreateAsync"/>;
/// the app migrates and seeds the database on start.
/// </summary>
public sealed class ApiFactory(string connectionString, FakeTimeProvider clock, IReadOnlyDictionary<string, string>? settings = null, bool realModel = false)
    : WebApplicationFactory<Program>
{
    /// <summary>Wednesday 25 November 2026, 09:00 in London.</summary>
    public static readonly DateTimeOffset DefaultNow = new(2026, 11, 25, 9, 0, 0, TimeSpan.Zero);

    public FakeTimeProvider Clock { get; } = clock;

    /// <summary>The model the assistant talks to: scripted per test, unless the factory was made with the real model.</summary>
    public StubChatClient Model { get; } = new();
    public string ConnectionString { get; } = connectionString;

    /// <param name="realModel">Keep the app's own model (OpenAI when OpenAI__ApiKey is set) instead of the stub; for evals.</param>
    public static async Task<ApiFactory> CreateAsync(DbFixture db, DateTimeOffset? now = null, IReadOnlyDictionary<string, string>? settings = null,
        bool realModel = false)
    {
        var factory = new ApiFactory(await db.CreateDatabaseAsync(), new FakeTimeProvider(now ?? DefaultNow), settings, realModel);
        // Start the app now, so the database is migrated and seeded before the test touches it.
        _ = factory.Server;
        return factory;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Db", ConnectionString);
        foreach (var (key, value) in settings ?? new Dictionary<string, string>())
        {
            builder.UseSetting(key, value);
        }
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
            if (!realModel)
            {
                services.RemoveAll<Microsoft.Extensions.AI.IChatClient>();
                services.AddSingleton<Microsoft.Extensions.AI.IChatClient>(Model);
            }
        });
    }

    public AppDb Db() => DbFixture.CreateDb(ConnectionString);

    /// <summary>A client that keeps cookies and does not follow redirects.</summary>
    public HttpClient Client() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    /// <summary>A client signed in as the given seeded user.</summary>
    public async Task<HttpClient> SignedInAsync(Guid userId)
    {
        var client = Client();
        var response = await client.PostAsJsonAsync("/api/auth/sign-in", new { userId });
        response.EnsureSuccessStatusCode();
        return client;
    }
}
