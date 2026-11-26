using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using ResourceBooking.Data;
using ResourceBooking.Data.Entities;
using ResourceBooking.Shared;
using ResourceBooking.Tests.Infrastructure;

namespace ResourceBooking.Tests.Database;

/// <summary>
/// Every added or changed user, resource and booking leaves one audit event in the same
/// transaction, with who did it and the fields that changed.
/// </summary>
public class AuditTests(DbFixture fixture) : IAsyncLifetime
{
    private static readonly Guid Sam = Guid.CreateVersion7();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 25, 9, 0, 0, TimeSpan.Zero));
    private string _connectionString = "";
    private Guid? _actor = Sam;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _connectionString = await fixture.CreateDatabaseAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private AppDb Db() => DbFixture.CreateDb(_connectionString, new AuditInterceptor(_clock, () => _actor));

    private static User NewUser(string name = "Cara") =>
        new() { Id = Guid.CreateVersion7(), Email = $"{name.ToLowerInvariant()}@sonicjoy-solutions.com", DisplayName = name, Role = Role.Employee, IsActive = true };

    private static Resource NewRoom() => new() { Id = Guid.CreateVersion7(), Name = "Avon", Type = ResourceType.MeetingRoom, Capacity = 4 };

    private static Booking NewBooking(Guid resourceId, Guid userId) => new()
    {
        Id = Guid.CreateVersion7(),
        ResourceId = resourceId,
        UserId = userId,
        StartsAt = new DateTimeOffset(2026, 11, 26, 10, 0, 0, TimeSpan.Zero),
        EndsAt = new DateTimeOffset(2026, 11, 26, 11, 0, 0, TimeSpan.Zero),
        BlockedUntil = new DateTimeOffset(2026, 11, 26, 11, 0, 0, TimeSpan.Zero),
        Attendees = 2,
        Status = BookingStatus.Confirmed,
    };

    private async Task<List<AuditEvent>> EventsAsync()
    {
        await using var db = DbFixture.CreateDb(_connectionString);
        return await db.AuditEvents.OrderBy(e => e.At).ThenBy(e => e.Id).ToListAsync(Ct);
    }

    [Fact]
    public async Task Adding_a_user_resource_and_booking_writes_one_added_event_each()
    {
        var user = NewUser();
        var room = NewRoom();
        var booking = NewBooking(room.Id, user.Id);
        await using (var db = Db())
        {
            db.AddRange(user, room, booking);
            await db.SaveChangesAsync(Ct);
        }

        var events = await EventsAsync();

        Assert.Equivalent(
            new[] { ("User", user.Id), ("Resource", room.Id), ("Booking", booking.Id) },
            events.Select(e => (e.EntityType, e.EntityId)), strict: true);
        Assert.All(events, e =>
        {
            Assert.Equal("Added", e.Action);
            Assert.Equal(Sam, e.ActorId);
            Assert.Equal(_clock.GetUtcNow(), e.At);
        });
        var userChanges = JsonDocument.Parse(events.Single(e => e.EntityType == "User").Changes).RootElement;
        Assert.Equal("Cara", userChanges.GetProperty("DisplayName")[1].GetString());
        Assert.Equal("Employee", userChanges.GetProperty("Role")[1].GetString());
    }

    [Fact]
    public async Task Changing_a_field_records_only_that_field_with_old_and_new_values()
    {
        var user = NewUser();
        var room = NewRoom();
        var booking = NewBooking(room.Id, user.Id);
        await using (var db = Db())
        {
            db.AddRange(user, room, booking);
            await db.SaveChangesAsync(Ct);
        }

        await using (var db = Db())
        {
            var stored = await db.Bookings.SingleAsync(b => b.Id == booking.Id, Ct);
            stored.Status = BookingStatus.Cancelled;
            stored.CancelReason = "Plans changed";
            await db.SaveChangesAsync(Ct);
        }

        var modified = Assert.Single(await EventsAsync(), e => e.Action == "Modified");
        var changes = JsonDocument.Parse(modified.Changes).RootElement;
        Assert.Equal(["CancelReason", "Status"], changes.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal("Confirmed", changes.GetProperty("Status")[0].GetString());
        Assert.Equal("Cancelled", changes.GetProperty("Status")[1].GetString());
    }

    [Fact]
    public async Task An_edit_saved_through_Update_records_the_real_old_values()
    {
        var room = NewRoom();
        await using (var db = Db())
        {
            db.Resources.Add(room);
            await db.SaveChangesAsync(Ct);
        }

        await using (var db = Db())
        {
            db.Resources.Update(new Resource
            {
                Id = room.Id,
                Name = room.Name,
                Type = room.Type,
                Capacity = 6,
                LastDay = new DateOnly(2026, 11, 27),
            });
            await db.SaveChangesAsync(Ct);
        }

        var modified = Assert.Single(await EventsAsync(), e => e.Action == "Modified");
        var changes = JsonDocument.Parse(modified.Changes).RootElement;
        Assert.Equal(["Capacity", "LastDay"], changes.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(4, changes.GetProperty("Capacity")[0].GetInt32());
        Assert.Equal("retired", Reports.HistoryLabels.Verb(modified));
    }

    [Fact]
    public async Task A_failed_save_leaves_nothing_queued_for_the_next_one()
    {
        var user = NewUser();
        var room = NewRoom();
        await using var db = Db();
        db.AddRange(user, room, NewBooking(room.Id, user.Id));
        await db.SaveChangesAsync(Ct);
        var before = (await EventsAsync()).Count;

        var clash = NewBooking(room.Id, user.Id);
        db.Bookings.Add(clash);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        db.Entry(clash).State = EntityState.Detached;
        db.Users.Add(NewUser("Dan"));
        await db.SaveChangesAsync(Ct);

        Assert.Equal(before + 1, (await EventsAsync()).Count);
    }

    [Fact]
    public async Task An_edit_marked_modified_on_an_attached_stub_is_recorded_with_the_stored_value()
    {
        var room = NewRoom();
        await using (var db = Db())
        {
            db.Resources.Add(room);
            await db.SaveChangesAsync(Ct);
        }

        await using (var db = Db())
        {
            var stub = new Resource { Id = room.Id, Name = room.Name, Type = room.Type, Capacity = 6 };
            db.Resources.Attach(stub);
            db.Entry(stub).Property(r => r.Capacity).IsModified = true;
            await db.SaveChangesAsync(Ct);
        }

        var modified = Assert.Single(await EventsAsync(), e => e.Action == "Modified");
        var changes = JsonDocument.Parse(modified.Changes).RootElement;
        Assert.Equal(4, changes.GetProperty("Capacity")[0].GetInt32());
        Assert.Equal(6, changes.GetProperty("Capacity")[1].GetInt32());
    }

    [Fact]
    public async Task A_property_set_on_an_attached_stub_records_the_stored_old_value_not_the_stubs()
    {
        var room = NewRoom();
        room.LastDay = new DateOnly(2026, 12, 31);
        await using (var db = Db())
        {
            db.Resources.Add(room);
            await db.SaveChangesAsync(Ct);
        }

        await using (var db = Db())
        {
            var stub = new Resource { Id = room.Id, Name = room.Name, Type = room.Type };
            db.Resources.Attach(stub);
            stub.Capacity = 6;
            await db.SaveChangesAsync(Ct);
        }

        var modified = Assert.Single(await EventsAsync(), e => e.Action == "Modified");
        var changes = JsonDocument.Parse(modified.Changes).RootElement;
        Assert.Equal(["Capacity"], changes.EnumerateObject().Select(p => p.Name));
        Assert.Equal(4, changes.GetProperty("Capacity")[0].GetInt32());
    }

    [Fact]
    public async Task A_synchronous_save_through_Update_records_the_real_old_values()
    {
        var room = NewRoom();
        await using (var db = Db())
        {
            db.Resources.Add(room);
            await db.SaveChangesAsync(Ct);
        }

        using (var db = Db())
        {
            db.Resources.Update(new Resource { Id = room.Id, Name = room.Name, Type = room.Type, Capacity = 6 });
            db.SaveChanges();
        }

        var modified = Assert.Single(await EventsAsync(), e => e.Action == "Modified");
        var changes = JsonDocument.Parse(modified.Changes).RootElement;
        Assert.Equal(["Capacity"], changes.EnumerateObject().Select(p => p.Name));
        Assert.Equal(4, changes.GetProperty("Capacity")[0].GetInt32());
    }

    [Fact]
    public async Task A_cancelled_save_leaves_nothing_queued_for_the_next_one()
    {
        await using var db = Db();
        db.Users.Add(NewUser("Dan"));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => db.SaveChangesAsync(cancelled.Token));

        await db.SaveChangesAsync(Ct);

        Assert.Single(await EventsAsync());
    }

    [Fact]
    public async Task Editing_a_row_deleted_underneath_is_a_concurrency_error_and_leaves_nothing_queued()
    {
        var user = NewUser();
        await using var db = Db();
        db.Users.Add(user);
        await db.SaveChangesAsync(Ct);
        await using (var other = DbFixture.CreateDb(_connectionString))
        {
            await other.Database.ExecuteSqlAsync($"DELETE FROM users WHERE id = {user.Id}", Ct);
        }

        user.DisplayName = "Changed";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db.SaveChangesAsync(Ct));
        db.Entry(user).State = EntityState.Detached;
        db.Users.Add(NewUser("Dan"));
        await db.SaveChangesAsync(Ct);

        Assert.Equal(2, (await EventsAsync()).Count);
    }

    [Fact]
    public async Task Saving_with_nothing_changed_writes_no_event()
    {
        var user = NewUser();
        await using (var db = Db())
        {
            db.Users.Add(user);
            await db.SaveChangesAsync(Ct);
        }
        await using (var db = Db())
        {
            var stored = await db.Users.SingleAsync(u => u.Id == user.Id, Ct);
            stored.DisplayName = "Cara";
            await db.SaveChangesAsync(Ct);
        }

        Assert.Single(await EventsAsync());
    }

    [Fact]
    public async Task A_refused_write_leaves_no_audit_event()
    {
        var user = NewUser();
        var room = NewRoom();
        await using (var db = Db())
        {
            db.AddRange(user, room, NewBooking(room.Id, user.Id));
            await db.SaveChangesAsync(Ct);
        }
        var before = (await EventsAsync()).Count;

        await using (var db = Db())
        {
            db.Bookings.Add(NewBooking(room.Id, user.Id));
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
            Assert.Equal("23P01", (error.InnerException as PostgresException)?.SqlState);
        }

        Assert.Equal(before, (await EventsAsync()).Count);
    }

    [Fact]
    public async Task Writes_without_a_signed_in_user_are_recorded_as_the_system()
    {
        _actor = null;
        await using (var db = Db())
        {
            db.Users.Add(NewUser());
            await db.SaveChangesAsync(Ct);
        }

        Assert.Null(Assert.Single(await EventsAsync()).ActorId);
    }

    [Fact]
    public async Task Notices_and_assistant_records_are_not_audited()
    {
        var user = NewUser();
        var room = NewRoom();
        var booking = NewBooking(room.Id, user.Id);
        await using (var db = Db())
        {
            db.AddRange(user, room, booking);
            await db.SaveChangesAsync(Ct);
        }
        var before = (await EventsAsync()).Count;

        await using (var db = Db())
        {
            db.Notices.Add(new Notice { Id = Guid.CreateVersion7(), UserId = user.Id, BookingId = booking.Id, Message = "Cancelled", CreatedAt = _clock.GetUtcNow() });
            db.TokenUsage.Add(new TokenUsage { Day = new DateOnly(2026, 11, 25), Tokens = 10 });
            await db.SaveChangesAsync(Ct);
        }

        Assert.Equal(before, (await EventsAsync()).Count);
    }
}
