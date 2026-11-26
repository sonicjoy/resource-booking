using Microsoft.EntityFrameworkCore;
using ResourceBooking.Data;
using ResourceBooking.Tests.Infrastructure;

namespace ResourceBooking.Tests.Api;

/// <summary>The running app records its writes: start-up seeding as the system, requests as the signed-in user.</summary>
public class AuditWiringTests(DbFixture fixture) : IAsyncLifetime
{
    private ApiFactory _app = null!;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _app = await ApiFactory.CreateAsync(fixture);

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task Start_up_seeding_is_recorded_as_the_system()
    {
        await using var db = _app.Db();

        var events = await db.AuditEvents.ToListAsync(Ct);

        Assert.Equal(3 + 4 + 9, events.Count);
        Assert.All(events, e => Assert.Null(e.ActorId));
    }

    [Fact]
    public async Task A_reseed_is_recorded_as_the_admin_who_ran_it()
    {
        var admin = await _app.SignedInAsync(Seed.SamId);

        (await admin.PostAsync("/api/admin/reseed", null, Ct)).EnsureSuccessStatusCode();

        await using var db = _app.Db();
        var events = await db.AuditEvents.ToListAsync(Ct);
        Assert.Equal(3 + 4 + 9, events.Count);
        Assert.All(events, e => Assert.Equal(Seed.SamId, e.ActorId));
    }
}
