using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using ResourceBooking.Data;
using ResourceBooking.Shared;
using ResourceBooking.Tests.Infrastructure;

namespace ResourceBooking.Tests.Database;

public class SeedTests(DbFixture fixture) : IAsyncLifetime
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    // Wednesday 25 November 2026, 09:00 in London (GMT, so UTC+0).
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 25, 9, 0, 0, TimeSpan.Zero));
    private string _connectionString = "";

    public async ValueTask InitializeAsync() => _connectionString = await fixture.CreateDatabaseAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Seeding_an_empty_database_creates_the_journey_data()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = DbFixture.CreateDb(_connectionString);

        await Seed.SeedIfEmptyAsync(db, _clock, London, ct);

        Assert.Equal(["Ana", "Ben", "Sam"], await db.Users.OrderBy(u => u.DisplayName).Select(u => u.DisplayName).ToListAsync(ct));
        Assert.Equal(Role.Admin, (await db.Users.SingleAsync(u => u.DisplayName == "Sam", ct)).Role);
        Assert.Equal(["Main Kitchen", "Pool Car 1", "Severn", "Thames"], await db.Resources.OrderBy(r => r.Name).Select(r => r.Name).ToListAsync(ct));

        var kitchen = await db.Resources.SingleAsync(r => r.Name == "Main Kitchen", ct);
        Assert.Equal((5, 25), (kitchen.Units, kitchen.Capacity));

        var car = await db.Resources.SingleAsync(r => r.Name == "Pool Car 1", ct);
        var tomorrowAtNine = new DateTimeOffset(2026, 11, 26, 9, 0, 0, TimeSpan.Zero);
        var carBooking = await db.Bookings.SingleAsync(b => b.ResourceId == car.Id && b.StartsAt == tomorrowAtNine, ct);
        Assert.Equal(tomorrowAtNine.AddHours(3), carBooking.BlockedUntil);
    }

    [Fact]
    public async Task Ben_has_two_future_bookings_and_there_is_past_history()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = DbFixture.CreateDb(_connectionString);

        await Seed.SeedIfEmptyAsync(db, _clock, London, ct);

        var now = _clock.GetUtcNow();
        Assert.Equal(2, await db.Bookings.CountAsync(b => b.UserId == Seed.BenId && b.StartsAt > now, ct));
        Assert.True(await db.Bookings.CountAsync(b => b.EndsAt < now, ct) >= 5);
        Assert.Contains(await db.Bookings.Where(b => b.EndsAt < now).ToListAsync(ct), b => b.EndsAt - b.StartsAt >= TimeSpan.FromDays(2));
    }

    [Fact]
    public async Task Local_times_are_converted_with_the_company_zone_in_summer()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = DbFixture.CreateDb(_connectionString);
        // Wednesday 8 July 2026, 09:00 in London (BST, UTC+1).
        var summer = new FakeTimeProvider(new DateTimeOffset(2026, 7, 8, 8, 0, 0, TimeSpan.Zero));

        await Seed.SeedIfEmptyAsync(db, summer, London, ct);

        var nineLondonTomorrow = new DateTimeOffset(2026, 7, 9, 8, 0, 0, TimeSpan.Zero);
        Assert.True(await db.Bookings.AnyAsync(b => b.ResourceId == Seed.PoolCar1Id && b.StartsAt == nineLondonTomorrow, ct));
    }

    [Fact]
    public async Task Seeded_bookings_have_the_same_ids_every_time()
    {
        var ct = TestContext.Current.CancellationToken;
        var other = await fixture.CreateDatabaseAsync();
        await using var first = DbFixture.CreateDb(_connectionString);
        await using var second = DbFixture.CreateDb(other);

        await Seed.SeedIfEmptyAsync(first, _clock, London, ct);
        await Seed.SeedIfEmptyAsync(second, _clock, London, ct);

        Assert.Equal(
            await first.Bookings.OrderBy(b => b.Id).Select(b => b.Id).ToListAsync(ct),
            await second.Bookings.OrderBy(b => b.Id).Select(b => b.Id).ToListAsync(ct));
    }

    [Fact]
    public async Task Seeding_twice_adds_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = DbFixture.CreateDb(_connectionString);

        await Seed.SeedIfEmptyAsync(db, _clock, London, ct);
        var bookings = await db.Bookings.CountAsync(ct);
        await Seed.SeedIfEmptyAsync(db, _clock, London, ct);

        Assert.Equal(3, await db.Users.CountAsync(ct));
        Assert.Equal(bookings, await db.Bookings.CountAsync(ct));
    }
}
