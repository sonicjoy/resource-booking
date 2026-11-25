using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using ResourceBooking.Data;
using ResourceBooking.Shared;
using ResourceBooking.Shared.Dtos;
using ResourceBooking.Tests.Infrastructure;

namespace ResourceBooking.Tests.Api;

public class AuthTests(DbFixture fixture) : IAsyncLifetime
{
    private ApiFactory _app = null!;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _app = await ApiFactory.CreateAsync(fixture);

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task Sign_in_options_list_the_active_users()
    {
        var options = await _app.Client().GetFromJsonAsync<List<SignInOption>>("/api/auth/sign-in-options", Ct);

        Assert.Equal(["Ana", "Ben", "Sam"], options!.Select(o => o.DisplayName).Order());
        Assert.Equal(Role.Admin, options!.Single(o => o.DisplayName == "Sam").Role);
    }

    [Fact]
    public async Task Enums_travel_as_names_not_numbers()
    {
        var json = await _app.Client().GetStringAsync("/api/auth/sign-in-options", Ct);

        Assert.Contains("\"role\":\"Admin\"", json);
    }

    [Fact]
    public async Task Me_without_signing_in_is_401_not_a_redirect()
    {
        var response = await _app.Client().GetAsync("/api/auth/me", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Signing_in_returns_who_you_are()
    {
        var client = await _app.SignedInAsync(Seed.AnaId);

        var me = await client.GetFromJsonAsync<CurrentUser>("/api/auth/me", Ct);

        Assert.Equal(new CurrentUser(Seed.AnaId, "Ana", "ana@sonicjoy-solutions.com", Role.Employee), me);
    }

    [Fact]
    public async Task An_unknown_or_inactive_user_cannot_sign_in()
    {
        await using (var db = _app.Db())
        {
            await db.Users.Where(u => u.Id == Seed.BenId).ExecuteUpdateAsync(u => u.SetProperty(x => x.IsActive, false), Ct);
        }

        var unknown = await _app.Client().PostAsJsonAsync("/api/auth/sign-in", new { userId = Guid.NewGuid() }, Ct);
        var inactive = await _app.Client().PostAsJsonAsync("/api/auth/sign-in", new { userId = Seed.BenId }, Ct);

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, inactive.StatusCode);
    }

    [Fact]
    public async Task Signing_out_ends_the_session()
    {
        var client = await _app.SignedInAsync(Seed.AnaId);

        (await client.PostAsync("/api/auth/sign-out", null, Ct)).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me", Ct)).StatusCode);
    }

    [Fact]
    public async Task An_employee_gets_403_on_an_admin_endpoint()
    {
        var client = await _app.SignedInAsync(Seed.AnaId);

        var response = await client.PostAsync("/api/admin/reseed", null, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_deactivated_users_open_session_is_rejected_on_its_next_request()
    {
        var client = await _app.SignedInAsync(Seed.BenId);
        await using (var db = _app.Db())
        {
            await db.Users.Where(u => u.Id == Seed.BenId).ExecuteUpdateAsync(u => u.SetProperty(x => x.IsActive, false), Ct);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me", Ct)).StatusCode);
    }

    [Fact]
    public async Task A_demoted_admin_loses_admin_access_at_once()
    {
        var client = await _app.SignedInAsync(Seed.SamId);
        await using (var db = _app.Db())
        {
            await db.Users.Where(u => u.Id == Seed.SamId).ExecuteUpdateAsync(u => u.SetProperty(x => x.Role, Role.Employee), Ct);
        }

        var response = await client.PostAsync("/api/admin/reseed", null, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_session_whose_user_no_longer_exists_is_rejected()
    {
        var client = await _app.SignedInAsync(Seed.AnaId);
        await using (var db = _app.Db())
        {
            await db.Database.ExecuteSqlRawAsync("DELETE FROM bookings WHERE user_id = {0}; DELETE FROM users WHERE id = {0};", [Seed.AnaId], Ct);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me", Ct)).StatusCode);
    }

    [Fact]
    public async Task Reseed_restores_the_seed_and_seeded_sessions_survive()
    {
        var admin = await _app.SignedInAsync(Seed.SamId);
        await using (var db = _app.Db())
        {
            await db.Database.ExecuteSqlRawAsync("DELETE FROM bookings", Ct);
            db.Users.Add(new Data.Entities.User { Id = Guid.CreateVersion7(), Email = "cara@sonicjoy-solutions.com", DisplayName = "Cara", Role = Role.Employee, IsActive = true });
            await db.SaveChangesAsync(Ct);
        }

        var response = await admin.PostAsync("/api/admin/reseed", null, Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await using var after = _app.Db();
        Assert.Equal(3, await after.Users.CountAsync(Ct));
        Assert.Equal(9, await after.Bookings.CountAsync(Ct));
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/auth/me", Ct)).StatusCode);
    }

    [Fact]
    public async Task Data_protection_keys_are_kept_in_the_database()
    {
        await _app.SignedInAsync(Seed.AnaId);

        await using var db = _app.Db();
        Assert.True(await db.DataProtectionKeys.AnyAsync(Ct));
    }

    [Fact]
    public async Task Start_up_seeds_with_the_injected_clock()
    {
        await using var summer = await ApiFactory.CreateAsync(fixture, new DateTimeOffset(2026, 7, 8, 8, 0, 0, TimeSpan.Zero));

        summer.Client().Dispose();

        await using var db = summer.Db();
        var nineLondonTomorrow = new DateTimeOffset(2026, 7, 9, 8, 0, 0, TimeSpan.Zero);
        Assert.True(await db.Bookings.AnyAsync(b => b.ResourceId == Seed.PoolCar1Id && b.StartsAt == nineLondonTomorrow, Ct));
    }
}
