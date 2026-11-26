using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ResourceBooking.Data;
using ResourceBooking.Shared;
using ResourceBooking.Tests.Infrastructure;

namespace ResourceBooking.Tests.Api;

/// <summary>Guarantees behind sign-in that the review found untested, and the hardening it asked for.</summary>
public class AuthHardeningTests(DbFixture fixture) : IAsyncLifetime
{
    // Endpoints anyone may call without signing in. Every other /api endpoint must require a user.
    private static readonly string[] Anonymous =
        ["GET /api/auth/sign-in-options", "POST /api/auth/sign-in", "POST /api/auth/sign-out"];

    private ApiFactory _app = null!;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _app = await ApiFactory.CreateAsync(fixture);

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public void Every_api_endpoint_requires_sign_in_unless_listed()
    {
        var endpoints = _app.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api", StringComparison.Ordinal) == true);

        Assert.All(endpoints, endpoint =>
        {
            var name = $"{endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Single()} {endpoint.RoutePattern.RawText}";
            var open = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;
            var guarded = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Count > 0;
            Assert.True(Anonymous.Contains(name) ? open : guarded && !open, name);
        });
    }

    [Fact]
    public async Task A_cross_site_post_is_refused()
    {
        var client = await _app.SignedInAsync(Seed.SamId);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/reseed");
        request.Headers.Add("Origin", "https://evil.example");

        var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_same_origin_post_is_allowed()
    {
        var client = await _app.SignedInAsync(Seed.SamId);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/reseed");
        request.Headers.Add("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));

        var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Reseed_is_limited_to_once_every_thirty_seconds()
    {
        var admin = await _app.SignedInAsync(Seed.SamId);

        var first = await admin.PostAsync("/api/admin/reseed", null, Ct);
        var second = await admin.PostAsync("/api/admin/reseed", null, Ct);

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        var body = await second.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("RATE_LIMITED", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Outside_development_the_session_cookie_is_always_secure()
    {
        await using var production = _app.WithWebHostBuilder(host => host.UseEnvironment("Production"));

        var response = await production.CreateClient().PostAsJsonAsync("/api/auth/sign-in", new { userId = Seed.AnaId }, Ct);

        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_session_survives_a_redeploy_on_the_same_database()
    {
        var cookie = (await _app.Client().PostAsJsonAsync("/api/auth/sign-in", new { userId = Seed.AnaId }, Ct))
            .Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        await using var redeployed = new ApiFactory(_app.ConnectionString, _app.Clock);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        request.Headers.Add("Cookie", cookie);

        var response = await redeployed.Client().SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_promoted_employees_session_is_rejected_until_they_sign_in_again()
    {
        var client = await _app.SignedInAsync(Seed.AnaId);
        await using (var db = _app.Db())
        {
            await db.Users.Where(u => u.Id == Seed.AnaId).ExecuteUpdateAsync(u => u.SetProperty(x => x.Role, Role.Admin), Ct);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me", Ct)).StatusCode);
    }

    [Fact]
    public async Task After_a_reseed_a_non_seeded_users_session_is_rejected_and_seed_rows_are_restored()
    {
        var caraId = Guid.CreateVersion7();
        await using (var db = _app.Db())
        {
            db.Users.Add(new Data.Entities.User { Id = caraId, Email = "cara@sonicjoy-solutions.com", DisplayName = "Cara", Role = Role.Employee, IsActive = true });
            await db.SaveChangesAsync(Ct);
            await db.Users.Where(u => u.Id == Seed.AnaId).ExecuteUpdateAsync(u => u.SetProperty(x => x.DisplayName, "Changed"), Ct);
        }
        var cara = await _app.SignedInAsync(caraId);
        var admin = await _app.SignedInAsync(Seed.SamId);

        (await admin.PostAsync("/api/admin/reseed", null, Ct)).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Unauthorized, (await cara.GetAsync("/api/auth/me", Ct)).StatusCode);
        await using var after = _app.Db();
        Assert.Equal("Ana", (await after.Users.SingleAsync(u => u.Id == Seed.AnaId, Ct)).DisplayName);
    }

    [Fact]
    public async Task Signing_in_as_someone_else_replaces_the_session()
    {
        var client = await _app.SignedInAsync(Seed.AnaId);

        (await client.PostAsJsonAsync("/api/auth/sign-in", new { userId = Seed.BenId }, Ct)).EnsureSuccessStatusCode();

        Assert.Equal("Ben", (await client.GetFromJsonAsync<JsonElement>("/api/auth/me", Ct)).GetProperty("displayName").GetString());
    }

    [Fact]
    public void Every_shared_enum_travels_by_name_only()
    {
        var enums = typeof(Role).Assembly.GetTypes().Where(t => t.IsEnum && t.IsPublic);

        Assert.All(enums, type =>
        {
            var number = JsonSerializer.Serialize(Enum.GetValues(type).GetValue(0), type);
            Assert.StartsWith("\"", number);
            Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize("5", type));
        });
    }
}
