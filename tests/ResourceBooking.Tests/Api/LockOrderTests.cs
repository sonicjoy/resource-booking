using System.Net;
using System.Net.Http.Json;
using Npgsql;
using ResourceBooking.Data;
using ResourceBooking.Tests.Infrastructure;

namespace ResourceBooking.Tests.Api;

/// <summary>
/// The lock order (design: "Lock order"; formal/AdminLocks.tla) against the real database: transactions held
/// open by the test interleave with real requests at the points the model says matter.
/// </summary>
public class LockOrderTests(DbFixture fixture) : IAsyncLifetime
{
    private const string Tomorrow = "2026-11-26";
    private ApiFactory _app = null!;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _app = await ApiFactory.CreateAsync(fixture);

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    /// <summary>A transaction the test holds open, taking row locks one statement at a time.</summary>
    private sealed class Held(NpgsqlConnection connection, NpgsqlTransaction transaction) : IAsyncDisposable
    {
        public static async Task<Held> OpenAsync(string connectionString)
        {
            var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(Ct);
            return new Held(connection, await connection.BeginTransactionAsync(Ct));
        }

        public async Task LockAsync(string table, Guid id, string mode)
        {
            await using var command = new NpgsqlCommand($"SELECT 1 FROM {table} WHERE id = @id {mode}", connection, transaction);
            command.Parameters.AddWithValue("id", id);
            await command.ExecuteNonQueryAsync(Ct);
        }

        public Task CommitAsync() => transaction.CommitAsync(Ct);

        public async ValueTask DisposeAsync()
        {
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    /// <summary>Waits until at least <paramref name="count"/> sessions on this database wait for a row lock.</summary>
    private async Task WaitForLockWaitersAsync(int count, Task? orDone = null)
    {
        await using var connection = new NpgsqlConnection(_app.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'", connection);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while ((long)(await command.ExecuteScalarAsync(Ct))! < count)
        {
            if (orDone is { IsCompleted: true })
            {
                return;
            }
            Assert.True(DateTime.UtcNow < deadline, $"fewer than {count} sessions ever waited for a lock");
            await Task.Delay(25, Ct);
        }
    }

    private static object LunchTo(Guid room) =>
        new { resourceId = Seed.MainKitchenId, deliveryAt = $"{Tomorrow}T12:30", attendees = 4, deliveryRoomId = room };

    [Fact]
    public async Task An_order_waiting_on_its_kitchen_is_refused_if_its_room_is_retired_meanwhile()
    {
        await using var kitchen = await Held.OpenAsync(_app.ConnectionString);
        await kitchen.LockAsync("resources", Seed.MainKitchenId, "FOR UPDATE");
        var ana = await _app.SignedInAsync(Seed.AnaId);
        var order = ana.PostAsJsonAsync("/api/bookings", LunchTo(Seed.ThamesId), Ct);
        await WaitForLockWaitersAsync(1);

        var sam = await _app.SignedInAsync(Seed.SamId);
        (await sam.PostAsJsonAsync($"/api/resources/{Seed.ThamesId}/retirement", new { lastDay = "2026-11-25" }, Ct)).EnsureSuccessStatusCode();
        await kitchen.CommitAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await order).StatusCode);
    }

    [Fact]
    public async Task An_admins_order_an_edit_and_a_deactivation_racing_do_not_deadlock()
    {
        // The edit holds the room and next wants a user the deactivation holds; the deactivation next wants the
        // admin placing the order. The order must not hold that admin while it waits for the room.
        await using var edit = await Held.OpenAsync(_app.ConnectionString);
        await using var deactivation = await Held.OpenAsync(_app.ConnectionString);
        await edit.LockAsync("resources", Seed.ThamesId, "FOR UPDATE");
        await deactivation.LockAsync("users", Seed.AnaId, "FOR NO KEY UPDATE");
        var sam = await _app.SignedInAsync(Seed.SamId);
        var order = sam.PostAsJsonAsync("/api/bookings", LunchTo(Seed.ThamesId), Ct);
        await WaitForLockWaitersAsync(1);

        // The order now waits. With the right order it holds nothing the deactivation wants, so this returns;
        // with the old order it holds Sam, and this waits too.
        var deactivationTakesSam = deactivation.LockAsync("users", Seed.SamId, "FOR NO KEY UPDATE");
        await WaitForLockWaitersAsync(2, deactivationTakesSam);
        var editTakesAna = edit.LockAsync("users", Seed.AnaId, "FOR SHARE");

        await deactivationTakesSam.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        await deactivation.CommitAsync();
        await editTakesAna.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        await edit.CommitAsync();
        Assert.Equal(HttpStatusCode.Created, (await order).StatusCode);
    }
}
