using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using ResourceBooking.Data;
using ResourceBooking.Shared;
using ResourceBooking.Tests.Infrastructure;

namespace ResourceBooking.Tests.Api;

/// <summary>Second-round fixes: configurable reseed window, 429 shape, strict enum names.</summary>
public class AuthRound2Tests(DbFixture fixture) : IAsyncLifetime
{
    private ApiFactory _app = null!;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _app = await ApiFactory.CreateAsync(fixture);

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task The_reseed_window_can_be_turned_off_for_end_to_end_runs()
    {
        await using var unlimited = _app.WithWebHostBuilder(host => host.UseSetting("Demo:ReseedWindowSeconds", "0"));
        var admin = await SignInAsync(unlimited.CreateClient(), Seed.SamId);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync("/api/admin/reseed", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync("/api/admin/reseed", null, Ct)).StatusCode);
    }

    [Fact]
    public async Task A_rate_limited_reseed_is_problem_details_with_retry_after()
    {
        var admin = await _app.SignedInAsync(Seed.SamId);
        await admin.PostAsync("/api/admin/reseed", null, Ct);

        var response = await admin.PostAsync("/api/admin/reseed", null, Ct);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.NotNull(response.Headers.RetryAfter);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;
        Assert.Equal("RATE_LIMITED", body.GetProperty("code").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("message").GetString()));
    }

    [Fact]
    public void Comma_joined_or_unknown_enum_names_are_refused()
    {
        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<ResourceType>("\"Car, Catering\""));
        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<ResourceType>("\"Boat\""));
        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<ResourceType>("\"2\""));
        Assert.Equal(ResourceType.Car, JsonSerializer.Deserialize<ResourceType>("\"Car\""));
    }

    private async Task<HttpClient> SignInAsync(HttpClient client, Guid userId)
    {
        var response = await client.PostAsync("/api/auth/sign-in",
            new StringContent($"{{\"userId\":\"{userId}\"}}", System.Text.Encoding.UTF8, "application/json"), Ct);
        response.EnsureSuccessStatusCode();
        return client;
    }
}
