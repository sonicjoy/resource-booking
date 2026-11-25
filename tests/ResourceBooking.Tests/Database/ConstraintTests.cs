using Microsoft.EntityFrameworkCore;
using Npgsql;
using ResourceBooking.Data;
using ResourceBooking.Data.Entities;
using ResourceBooking.Shared;
using ResourceBooking.Tests.Infrastructure;

namespace ResourceBooking.Tests.Database;

/// <summary>
/// The database itself must refuse double bookings (Goal 1), whatever code path wrote them.
/// These tests write straight through the DbContext, with no service logic in between.
/// </summary>
public class ConstraintTests(DbFixture fixture) : IAsyncLifetime
{
    private const string ExclusionViolation = "23P01";
    private const string CheckViolation = "23514";

    private static readonly DateTimeOffset Ten = new(2030, 1, 7, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid UserId = Guid.CreateVersion7();
    private static readonly Guid RoomId = Guid.CreateVersion7();
    private static readonly Guid CarId = Guid.CreateVersion7();
    private static readonly Guid KitchenId = Guid.CreateVersion7();

    private string _connectionString = "";

    public async ValueTask InitializeAsync()
    {
        _connectionString = await fixture.CreateDatabaseAsync();
        await using var db = Db();
        db.Users.Add(new User { Id = UserId, Email = "ana@sonicjoy-solutions.com", DisplayName = "Ana", Role = Role.Employee, IsActive = true });
        db.Resources.AddRange(
            new Resource { Id = RoomId, Name = "Thames", Type = ResourceType.MeetingRoom, Capacity = 8 },
            new Resource { Id = CarId, Name = "Pool Car 1", Type = ResourceType.Car, Capacity = 4, Turnaround = TimeSpan.FromMinutes(60) },
            new Resource { Id = KitchenId, Name = "Main Kitchen", Type = ResourceType.Catering, Capacity = 25, Units = 5 });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Overlapping_booking_on_the_same_resource_is_refused()
    {
        Assert.Null(await InsertAsync(NewBooking(RoomId, Ten, Ten.AddHours(1))));

        Assert.Equal(ExclusionViolation, await InsertAsync(NewBooking(RoomId, Ten.AddMinutes(30), Ten.AddMinutes(90))));
    }

    [Fact]
    public async Task Back_to_back_bookings_do_not_clash()
    {
        Assert.Null(await InsertAsync(NewBooking(RoomId, Ten, Ten.AddHours(1))));

        Assert.Null(await InsertAsync(NewBooking(RoomId, Ten.AddHours(1), Ten.AddHours(2))));
    }

    [Fact]
    public async Task Cancelled_bookings_do_not_block_the_slot()
    {
        Assert.Null(await InsertAsync(NewBooking(RoomId, Ten, Ten.AddHours(1), status: BookingStatus.Cancelled)));

        Assert.Null(await InsertAsync(NewBooking(RoomId, Ten, Ten.AddHours(1))));
    }

    [Fact]
    public async Task Booking_inside_the_turnaround_gap_is_refused()
    {
        Assert.Null(await InsertAsync(NewBooking(CarId, Ten, Ten.AddHours(1), gap: TimeSpan.FromMinutes(60))));

        Assert.Equal(ExclusionViolation,
            await InsertAsync(NewBooking(CarId, Ten.AddMinutes(90), Ten.AddHours(3), gap: TimeSpan.FromMinutes(60))));
    }

    [Fact]
    public async Task Same_kitchen_unit_on_the_same_day_is_refused_but_another_unit_is_allowed()
    {
        var day = new DateTimeOffset(2030, 1, 7, 0, 0, 0, TimeSpan.Zero);
        Assert.Null(await InsertAsync(NewBooking(KitchenId, day, day.AddDays(1), unit: 0)));

        Assert.Equal(ExclusionViolation, await InsertAsync(NewBooking(KitchenId, day, day.AddDays(1), unit: 0)));
        Assert.Null(await InsertAsync(NewBooking(KitchenId, day, day.AddDays(1), unit: 1)));
    }

    [Fact]
    public async Task Twenty_concurrent_inserts_for_one_slot_save_exactly_one()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => Task.Run(() => InsertAsync(NewBooking(RoomId, Ten, Ten.AddHours(1))))));

        Assert.Equal(1, results.Count(r => r is null));
        Assert.Equal(19, results.Count(r => r == ExclusionViolation));
    }

    [Fact]
    public async Task A_booking_must_end_after_it_starts()
    {
        Assert.Equal(CheckViolation, await InsertAsync(NewBooking(RoomId, Ten, Ten)));
    }

    [Fact]
    public async Task Blocked_until_cannot_be_before_the_end()
    {
        var booking = NewBooking(RoomId, Ten, Ten.AddHours(1));
        booking.BlockedUntil = Ten.AddMinutes(30);

        Assert.Equal(CheckViolation, await InsertAsync(booking));
    }

    [Fact]
    public async Task A_negative_unit_number_is_refused()
    {
        Assert.Equal(CheckViolation, await InsertAsync(NewBooking(RoomId, Ten, Ten.AddHours(1), unit: -1)));
    }

    [Fact]
    public async Task A_status_other_than_confirmed_or_cancelled_is_refused()
    {
        var booking = NewBooking(RoomId, Ten, Ten.AddHours(1));
        Assert.Null(await InsertAsync(booking));

        await using var db = Db();
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlAsync(
            $"UPDATE bookings SET status = 'Pending' WHERE id = {booking.Id}", TestContext.Current.CancellationToken));
        Assert.Equal(CheckViolation, error.SqlState);
    }

    private AppDb Db() => DbFixture.CreateDb(_connectionString);

    private static Booking NewBooking(Guid resourceId, DateTimeOffset start, DateTimeOffset end,
        TimeSpan gap = default, int unit = 0, BookingStatus status = BookingStatus.Confirmed) => new()
        {
            Id = Guid.CreateVersion7(),
            ResourceId = resourceId,
            UserId = UserId,
            UnitNo = unit,
            StartsAt = start,
            EndsAt = end,
            BlockedUntil = end + gap,
            Attendees = 1,
            Status = status,
        };

    /// <summary>Inserts the booking in its own context; returns null on success or the PostgreSQL error code.</summary>
    private async Task<string?> InsertAsync(Booking booking)
    {
        await using var db = Db();
        db.Bookings.Add(booking);
        try
        {
            await db.SaveChangesAsync();
            return null;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg)
        {
            return pg.SqlState;
        }
    }
}
