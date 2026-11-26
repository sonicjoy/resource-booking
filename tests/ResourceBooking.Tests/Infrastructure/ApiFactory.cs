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
public sealed class ApiFactory(string connectionString, FakeTimeProvider clock) : WebApplicationFactory<Program>
{
    /// <summary>Wednesday 25 November 2026, 09:00 in London.</summary>
    public static readonly DateTimeOffset DefaultNow = new(2026, 11, 25, 9, 0, 0, TimeSpan.Zero);

    public FakeTimeProvider Clock { get; } = clock;
    public string ConnectionString { get; } = connectionString;

    public static async Task<ApiFactory> CreateAsync(DbFixture db, DateTimeOffset? now = null)
    {
        var factory = new ApiFactory(await db.CreateDatabaseAsync(), new FakeTimeProvider(now ?? DefaultNow));
        // Start the app now, so the database is migrated and seeded before the test touches it.
        _ = factory.Server;
        return factory;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Db", ConnectionString);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
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
