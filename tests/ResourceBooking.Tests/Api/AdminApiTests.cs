using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ResourceBooking.Data;
using ResourceBooking.Data.Entities;
using ResourceBooking.Reports;
using ResourceBooking.Shared;
using ResourceBooking.Shared.Dtos;
using ResourceBooking.Tests.Infrastructure;

namespace ResourceBooking.Tests.Api;

/// <summary>
/// Users, resources, retirement, deactivation and history, against the seed. The clock is Wednesday
/// 25 November 2026, 09:00 in London; "tomorrow" is Thursday 26 November.
/// </summary>
public class AdminApiTests(DbFixture fixture) : IAsyncLifetime
{
    private const string Tomorrow = "2026-11-26";
    private static readonly DateOnly Today = new(2026, 11, 25);
    private ApiFactory _app = null!;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _app = await ApiFactory.CreateAsync(fixture);

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    private Task<HttpClient> SamAsync() => _app.SignedInAsync(Seed.SamId);

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.TryGetProperty("code", out var c) ? c.GetString() : null;

    private static object PoolCar2 => new
    {
        name = "Pool Car 2",
        type = "Car",
        capacity = 4,
        opensAt = "07:00",
        closesAt = "19:00",
        maxDuration = "3.00:00:00",
        turnaround = "01:00:00",
    };

    // ---- access ----

    public static TheoryData<string, string> AdminRoutes => new()
    {
        { "GET", "/api/users" },
        { "POST", "/api/users" },
        { "POST", "/api/resources" },
        { "POST", $"/api/resources/{Seed.SevernId}/retirement" },
        { "POST", $"/api/users/{Seed.BenId}/deactivation" },
        { "GET", "/api/reports/utilisation?from=2026-11-18&to=2026-11-24" },
        { "GET", "/api/history" },
    };

    [Theory]
    [MemberData(nameof(AdminRoutes))]
    public async Task Employees_get_403_from_every_admin_route(string method, string url)
    {
        var ana = await _app.SignedInAsync(Seed.AnaId);

        var response = await ana.SendAsync(new HttpRequestMessage(new HttpMethod(method), url) { Content = JsonContent.Create(new { }) }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---- users ----

    [Fact]
    public async Task An_added_user_is_stored_lower_case_and_can_sign_in()
    {
        var sam = await SamAsync();

        var response = await sam.PostAsJsonAsync("/api/users", new { email = " Cara@SonicJoy-Solutions.com ", displayName = "Cara", role = "Employee" }, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var cara = (await response.Content.ReadFromJsonAsync<UserDto>(Ct))!;
        Assert.Equal(("cara@sonicjoy-solutions.com", true), (cara.Email, cara.IsActive));
        Assert.Equal(HttpStatusCode.OK, (await _app.Client().PostAsJsonAsync("/api/auth/sign-in", new { userId = cara.Id }, Ct)).StatusCode);
    }

    [Fact]
    public async Task An_email_already_in_use_in_any_case_is_409_duplicate()
    {
        var sam = await SamAsync();

        var response = await sam.PostAsJsonAsync("/api/users", new { email = "ANA@sonicjoy-solutions.com", displayName = "Ana 2" }, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("DUPLICATE", await CodeAsync(response));
    }

    [Fact]
    public async Task Editing_a_user_cannot_change_whether_they_are_active()
    {
        var sam = await SamAsync();

        var response = await sam.PutAsJsonAsync($"/api/users/{Seed.BenId}",
            new { email = "ben@sonicjoy-solutions.com", displayName = "Benjamin", role = "Employee", isActive = false }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var ben = (await response.Content.ReadFromJsonAsync<UserDto>(Ct))!;
        Assert.Equal(("Benjamin", true), (ben.DisplayName, ben.IsActive));
    }

    // ---- resources ----

    [Fact]
    public async Task An_added_car_takes_its_rules_and_refuses_a_booking_longer_than_its_maximum()
    {
        var sam = await SamAsync();
        var created = await sam.PostAsJsonAsync("/api/resources", PoolCar2, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var car = (await created.Content.ReadFromJsonAsync<ResourceDto>(Ct))!;
        var ana = await _app.SignedInAsync(Seed.AnaId);

        var response = await ana.PostAsJsonAsync("/api/bookings",
            new { resourceId = car.Id, start = $"{Tomorrow}T09:00", end = "2026-11-30T09:00", attendees = 2 }, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("Pool Car 2 can be booked for 3 days at most", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Rule_fields_a_type_does_not_use_are_cleared()
    {
        var sam = await SamAsync();

        var response = await sam.PostAsJsonAsync("/api/resources",
            new { name = "Tyne", type = "MeetingRoom", capacity = 6, units = 4, turnaround = "00:30:00", maxDaysAhead = 30 }, Ct);

        var room = (await response.Content.ReadFromJsonAsync<ResourceDto>(Ct))!;
        Assert.Equal((1, (TimeSpan?)null, (int?)30), (room.Units, room.Turnaround, room.MaxDaysAhead));
    }

    [Fact]
    public async Task A_name_already_in_use_is_409_duplicate()
    {
        var sam = await SamAsync();

        var response = await sam.PostAsJsonAsync("/api/resources", new { name = "Thames", type = "MeetingRoom", capacity = 4 }, Ct);

        Assert.Equal("DUPLICATE", await CodeAsync(response));
    }

    [Fact]
    public async Task Editing_a_resource_cannot_change_its_type_or_last_day()
    {
        var sam = await SamAsync();

        var typeChange = await sam.PutAsJsonAsync($"/api/resources/{Seed.SevernId}", new { name = "Severn", type = "Car", capacity = 4 }, Ct);
        var lastDay = await sam.PutAsJsonAsync($"/api/resources/{Seed.SevernId}",
            new { name = "Severn", type = "MeetingRoom", capacity = 12, lastDay = Tomorrow }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, typeChange.StatusCode);
        Assert.Equal(HttpStatusCode.OK, lastDay.StatusCode);
        Assert.Null((await lastDay.Content.ReadFromJsonAsync<ResourceDto>(Ct))!.LastDay);
    }

    [Fact]
    public async Task Lowering_a_kitchens_units_below_one_holding_a_future_order_is_422_units_in_use()
    {
        var ana = await _app.SignedInAsync(Seed.AnaId);
        for (var i = 0; i < 5; i++)
        {
            (await ana.PostAsJsonAsync("/api/bookings",
                new { resourceId = Seed.MainKitchenId, deliveryAt = $"{Tomorrow}T12:30", attendees = 4, deliveryRoomId = Seed.ThamesId }, Ct)).EnsureSuccessStatusCode();
        }
        var sam = await SamAsync();
        object Kitchen(int units) => new { name = "Main Kitchen", type = "Catering", capacity = 25, units, opensAt = "08:00", closesAt = "16:00", maxDaysAhead = 90 };

        var lower = await sam.PutAsJsonAsync($"/api/resources/{Seed.MainKitchenId}", Kitchen(4), Ct);
        var same = await sam.PutAsJsonAsync($"/api/resources/{Seed.MainKitchenId}", Kitchen(5), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, lower.StatusCode);
        Assert.Equal("UNITS_IN_USE", await CodeAsync(lower));
        Assert.Equal(HttpStatusCode.OK, same.StatusCode);
    }

    // ---- retirement ----

    [Fact]
    public async Task Retiring_cancels_later_bookings_notifies_their_owners_and_refuses_new_ones()
    {
        var sam = await SamAsync();

        var response = await sam.PostAsJsonAsync($"/api/resources/{Seed.SevernId}/retirement", new { lastDay = Tomorrow }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, (await response.Content.ReadFromJsonAsync<CascadeResult>(Ct))!.Cancelled);
        var ana = await _app.SignedInAsync(Seed.AnaId);
        var notices = (await ana.GetFromJsonAsync<List<NoticeDto>>("/api/notices", Ct))!;
        Assert.Contains(notices, n => n.Message.Contains("Severn", StringComparison.Ordinal));
        var after = await ana.PostAsJsonAsync("/api/bookings",
            new { resourceId = Seed.SevernId, start = "2026-11-27T10:00", end = "2026-11-27T11:00", attendees = 2 }, Ct);
        Assert.Contains("RESOURCE_RETIRED", await after.Content.ReadAsStringAsync(Ct));
        var onLastDay = await ana.PostAsJsonAsync("/api/bookings",
            new { resourceId = Seed.SevernId, start = $"{Tomorrow}T10:00", end = $"{Tomorrow}T11:00", attendees = 2 }, Ct);
        Assert.Equal(HttpStatusCode.Created, onLastDay.StatusCode);
    }

    [Fact]
    public async Task A_last_day_before_today_is_422_last_day_past()
    {
        var sam = await SamAsync();

        var response = await sam.PostAsJsonAsync($"/api/resources/{Seed.SevernId}/retirement", new { lastDay = "2026-11-24" }, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("LAST_DAY_PAST", await CodeAsync(response));
    }

    [Fact]
    public async Task A_booking_running_across_the_last_day_is_422_in_use()
    {
        var ana = await _app.SignedInAsync(Seed.AnaId);
        (await ana.PostAsJsonAsync("/api/bookings",
            new { resourceId = Seed.PoolCar1Id, start = "2026-11-27T09:00", end = "2026-11-29T17:00", attendees = 2 }, Ct)).EnsureSuccessStatusCode();
        var sam = await SamAsync();

        var response = await sam.PostAsJsonAsync($"/api/resources/{Seed.PoolCar1Id}/retirement", new { lastDay = "2026-11-28" }, Ct);

        Assert.Equal("IN_USE", await CodeAsync(response));
        await using var db = _app.Db();
        Assert.Null((await db.Resources.SingleAsync(r => r.Id == Seed.PoolCar1Id, Ct)).LastDay);
    }

    [Fact]
    public async Task Bookings_racing_a_retirement_never_leave_one_confirmed_after_the_last_day()
    {
        var sam = await SamAsync();
        var ana = await _app.SignedInAsync(Seed.AnaId);
        var bookings = Enumerable.Range(8, 9).Select(hour => ana.PostAsJsonAsync("/api/bookings",
            new { resourceId = Seed.SevernId, start = $"2026-11-30T{hour:00}:00", end = $"2026-11-30T{hour:00}:45", attendees = 2 }, Ct));
        var retirement = sam.PostAsJsonAsync($"/api/resources/{Seed.SevernId}/retirement", new { lastDay = Tomorrow }, Ct);

        await Task.WhenAll(bookings.Append(retirement));

        Assert.Equal(HttpStatusCode.OK, (await retirement).StatusCode);
        await using var db = _app.Db();
        var lastDayEnds = new DateTimeOffset(2026, 11, 27, 0, 0, 0, TimeSpan.Zero);
        Assert.False(await db.Bookings.AnyAsync(b => b.ResourceId == Seed.SevernId && b.Status == BookingStatus.Confirmed && b.EndsAt > lastDayEnds, Ct));
    }

    // ---- deactivation ----

    [Fact]
    public async Task Deactivating_cancels_future_bookings_without_a_notice_and_ends_the_session()
    {
        var ben = await _app.SignedInAsync(Seed.BenId);
        var sam = await SamAsync();

        var response = await sam.PostAsync($"/api/users/{Seed.BenId}/deactivation", null, Ct);

        Assert.Equal(2, (await response.Content.ReadFromJsonAsync<CascadeResult>(Ct))!.Cancelled);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ben.GetAsync("/api/auth/me", Ct)).StatusCode);
        await using var db = _app.Db();
        Assert.False(await db.Notices.AnyAsync(n => n.UserId == Seed.BenId, Ct));
        Assert.False(await db.Bookings.AnyAsync(b => b.UserId == Seed.BenId && b.Status == BookingStatus.Confirmed && b.StartsAt > _app.Clock.GetUtcNow(), Ct));
    }

    [Fact]
    public async Task The_last_active_admin_cannot_be_deactivated_or_demoted()
    {
        var sam = await SamAsync();

        var deactivate = await sam.PostAsync($"/api/users/{Seed.SamId}/deactivation", null, Ct);
        var demote = await sam.PutAsJsonAsync($"/api/users/{Seed.SamId}", new { email = "sam@sonicjoy-solutions.com", displayName = "Sam", role = "Employee" }, Ct);

        Assert.Equal("LAST_ADMIN", await CodeAsync(deactivate));
        Assert.Equal("LAST_ADMIN", await CodeAsync(demote));
    }

    [Fact]
    public async Task Two_admins_deactivating_each_other_at_once_leave_one_admin()
    {
        var sam = await SamAsync();
        var dee = (await (await sam.PostAsJsonAsync("/api/users", new { email = "dee@sonicjoy-solutions.com", displayName = "Dee", role = "Admin" }, Ct))
            .Content.ReadFromJsonAsync<UserDto>(Ct))!;
        var deeClient = await _app.SignedInAsync(dee.Id);

        var results = await Task.WhenAll(
            sam.PostAsync($"/api/users/{dee.Id}/deactivation", null, Ct),
            deeClient.PostAsync($"/api/users/{Seed.SamId}/deactivation", null, Ct));

        await using var db = _app.Db();
        Assert.Equal(1, await db.Users.CountAsync(u => u.Role == Role.Admin && u.IsActive, Ct));
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.OK);
    }

    // ---- history ----

    [Fact]
    public async Task History_lists_additions_under_the_admins_name_newest_first()
    {
        var sam = await SamAsync();
        (await sam.PostAsJsonAsync("/api/users", new { email = "cara@sonicjoy-solutions.com", displayName = "Cara" }, Ct)).EnsureSuccessStatusCode();
        _app.Clock.Advance(TimeSpan.FromMinutes(1));
        (await sam.PostAsJsonAsync("/api/resources", PoolCar2, Ct)).EnsureSuccessStatusCode();

        var history = (await sam.GetFromJsonAsync<HistoryPage>("/api/history?from=2026-11-25&to=2026-11-25", Ct))!.Items;

        Assert.Equal([("Resource", "Pool Car 2", "added", "Sam"), ("User", "Cara", "added", "Sam")],
            history.Take(2).Select(h => (h.EntityType, h.Entity, h.Verb, h.Actor)));
        Assert.Contains(history[0].Changes, c => c.Field == "MaxDuration" && c.New == "3.00:00:00");
    }

    [Fact]
    public async Task History_describes_retirement_and_cancellation_and_filters_by_entity()
    {
        var sam = await SamAsync();
        (await sam.PostAsJsonAsync($"/api/resources/{Seed.SevernId}/retirement", new { lastDay = Tomorrow }, Ct)).EnsureSuccessStatusCode();

        var severn = (await sam.GetFromJsonAsync<HistoryPage>($"/api/history?entityType=Resource&entityId={Seed.SevernId}", Ct))!.Items;
        var bookings = (await sam.GetFromJsonAsync<HistoryPage>("/api/history?entityType=Booking&from=2026-11-25&to=2026-11-25", Ct))!.Items;

        Assert.Equal("retired", severn[0].Verb);
        Assert.All(severn, h => Assert.Equal(Seed.SevernId, h.EntityId));
        var cancelled = Assert.Single(bookings, h => h.Verb == "cancelled");
        Assert.StartsWith("Severn,", cancelled.Entity);
        Assert.Equal("Sam", cancelled.Actor);
    }

    [Fact]
    public async Task Seeding_appears_in_history_as_the_system()
    {
        var sam = await SamAsync();

        var history = (await sam.GetFromJsonAsync<HistoryPage>($"/api/history?entityType=User&entityId={Seed.AnaId}", Ct))!.Items;

        Assert.Equal(("added", "System"), (history.Single().Verb, history.Single().Actor));
    }

    // ---- concurrency, limits and history ----

    [Fact]
    public async Task History_says_when_it_stops_short_of_everything_that_matched()
    {
        await using (var db = _app.Db())
        {
            db.AuditEvents.AddRange(Enumerable.Range(0, History.Limit + 1).Select(i => new AuditEvent
            {
                Id = Guid.CreateVersion7(),
                At = _app.Clock.GetUtcNow(),
                EntityType = "Resource",
                EntityId = Seed.ThamesId,
                Action = "Modified",
                Changes = "{\"Capacity\":[8,8]}",
            }));
            await db.SaveChangesAsync(Ct);
        }
        var sam = await SamAsync();

        var all = (await sam.GetFromJsonAsync<HistoryPage>("/api/history", Ct))!;
        var ana = (await sam.GetFromJsonAsync<HistoryPage>($"/api/history?entityType=User&entityId={Seed.AnaId}", Ct))!;

        Assert.Equal((History.Limit, true), (all.Items.Count, all.More));
        Assert.Equal((1, false), (ana.Items.Count, ana.More));
    }

    [Fact]
    public async Task A_retirement_and_a_deactivation_at_once_cancel_each_booking_once()
    {
        var ben = await _app.SignedInAsync(Seed.BenId);
        for (var day = 27; day <= 30; day++)
        {
            (await ben.PostAsJsonAsync("/api/bookings",
                new { resourceId = Seed.SevernId, start = $"2026-11-{day}T14:00", end = $"2026-11-{day}T15:00", attendees = 2 }, Ct)).EnsureSuccessStatusCode();
        }
        var sam = await SamAsync();

        var results = await Task.WhenAll(
            sam.PostAsJsonAsync($"/api/resources/{Seed.SevernId}/retirement", new { lastDay = Tomorrow }, Ct),
            sam.PostAsync($"/api/users/{Seed.BenId}/deactivation", null, Ct));

        var counts = await Task.WhenAll(results.Select(async r => (await r.Content.ReadFromJsonAsync<CascadeResult>(Ct))!.Cancelled));
        await using var db = _app.Db();
        var cancelled = await db.Bookings.Where(b => b.Status == BookingStatus.Cancelled).Select(b => b.Id).ToListAsync(Ct);
        Assert.Equal(cancelled.Count, counts.Sum());
        var cancelEvents = await db.AuditEvents.CountAsync(e => e.Action == "Modified" && cancelled.Contains(e.EntityId), Ct);
        Assert.Equal(cancelled.Count, cancelEvents);
    }

    [Fact]
    public async Task A_booking_racing_a_retirement_never_runs_across_the_last_day()
    {
        var sam = await SamAsync();
        var ana = await _app.SignedInAsync(Seed.AnaId);
        for (var round = 0; round < 5; round++)
        {
            var created = await sam.PostAsJsonAsync("/api/resources", new
            {
                name = $"Race car {round}",
                type = "Car",
                capacity = 4,
                maxDuration = "3.00:00:00",
            }, Ct);
            var car = (await created.Content.ReadFromJsonAsync<ResourceDto>(Ct))!;

            await Task.WhenAll(
                ana.PostAsJsonAsync("/api/bookings", new { resourceId = car.Id, start = "2026-11-27T09:00", end = "2026-11-29T09:00", attendees = 2 }, Ct),
                sam.PostAsJsonAsync($"/api/resources/{car.Id}/retirement", new { lastDay = "2026-11-27" }, Ct));

            await using var db = _app.Db();
            var retired = (await db.Resources.SingleAsync(r => r.Id == car.Id, Ct)).LastDay is not null;
            var across = await db.Bookings.AnyAsync(b => b.ResourceId == car.Id && b.Status == BookingStatus.Confirmed, Ct);
            Assert.False(retired && across, $"round {round}: retired with a booking running past the last day");
        }
    }

    [Fact]
    public async Task Retiring_a_kitchen_keeps_orders_on_its_last_day_and_cancels_later_ones()
    {
        var ana = await _app.SignedInAsync(Seed.AnaId);
        object Order(string day) => new { resourceId = Seed.MainKitchenId, deliveryAt = $"{day}T12:30", attendees = 4, deliveryRoomId = Seed.ThamesId };
        var last = (await (await ana.PostAsJsonAsync("/api/bookings", Order(Tomorrow), Ct)).Content.ReadFromJsonAsync<BookingDto>(Ct))!;
        var after = (await (await ana.PostAsJsonAsync("/api/bookings", Order("2026-11-27"), Ct)).Content.ReadFromJsonAsync<BookingDto>(Ct))!;
        var sam = await SamAsync();

        (await sam.PostAsJsonAsync($"/api/resources/{Seed.MainKitchenId}/retirement", new { lastDay = Tomorrow }, Ct)).EnsureSuccessStatusCode();

        await using var db = _app.Db();
        Assert.Equal(BookingStatus.Confirmed, (await db.Bookings.SingleAsync(b => b.Id == last.Id, Ct)).Status);
        Assert.Equal(BookingStatus.Cancelled, (await db.Bookings.SingleAsync(b => b.Id == after.Id, Ct)).Status);
    }

    [Fact]
    public async Task A_retirement_without_a_last_day_or_a_resource_without_a_type_is_400()
    {
        var sam = await SamAsync();

        var retirement = await sam.PostAsJsonAsync($"/api/resources/{Seed.SevernId}/retirement", new { }, Ct);
        var resource = await sam.PostAsJsonAsync("/api/resources", new { name = "Typeless", capacity = 4 }, Ct);

        Assert.Equal((HttpStatusCode.BadRequest, HttpStatusCode.BadRequest), (retirement.StatusCode, resource.StatusCode));
    }

    [Fact]
    public async Task Renaming_to_a_name_or_email_in_use_is_409_duplicate()
    {
        var sam = await SamAsync();

        var user = await sam.PutAsJsonAsync($"/api/users/{Seed.BenId}", new { email = "ANA@sonicjoy-solutions.com", displayName = "Ben" }, Ct);
        var resource = await sam.PutAsJsonAsync($"/api/resources/{Seed.SevernId}", new { name = "Thames", type = "MeetingRoom", capacity = 12 }, Ct);

        Assert.Equal(("DUPLICATE", "DUPLICATE"), (await CodeAsync(user), await CodeAsync(resource)));
    }

    [Fact]
    public async Task Every_admin_write_appears_in_history_under_the_admin()
    {
        var sam = await SamAsync();
        (await sam.PutAsJsonAsync($"/api/users/{Seed.AnaId}", new { email = "ana@sonicjoy-solutions.com", displayName = "Ana B" }, Ct)).EnsureSuccessStatusCode();
        (await sam.PutAsJsonAsync($"/api/resources/{Seed.SevernId}", new { name = "Severn", type = "MeetingRoom", capacity = 14 }, Ct)).EnsureSuccessStatusCode();
        (await sam.PostAsync($"/api/users/{Seed.BenId}/deactivation", null, Ct)).EnsureSuccessStatusCode();

        var history = (await sam.GetFromJsonAsync<HistoryPage>("/api/history?from=2026-11-25&to=2026-11-25", Ct))!.Items
            .Where(h => h.Actor == "Sam").ToList();

        Assert.Contains(history, h => (h.Entity, h.Verb) == ("Ana B", "changed") && h.Changes.Any(c => c == new FieldChange("DisplayName", "Ana", "Ana B")));
        Assert.Contains(history, h => (h.Entity, h.Verb) == ("Severn", "changed") && h.Changes.Any(c => c == new FieldChange("Capacity", "12", "14")));
        Assert.Contains(history, h => (h.Entity, h.Verb) == ("Ben", "deactivated"));
        Assert.Equal(2, history.Count(h => h.EntityType == "Booking" && h.Verb == "cancelled"));
    }

    [Fact]
    public async Task Editing_a_user_without_a_role_keeps_the_role_they_have()
    {
        var sam = await SamAsync();
        var dee = (await (await sam.PostAsJsonAsync("/api/users", new { email = "dee@sonicjoy-solutions.com", displayName = "Dee", role = "Admin" }, Ct))
            .Content.ReadFromJsonAsync<UserDto>(Ct))!;

        var response = await sam.PutAsJsonAsync($"/api/users/{dee.Id}", new { email = "dee@sonicjoy-solutions.com", displayName = "Dee Two" }, Ct);

        Assert.Equal(Role.Admin, (await response.Content.ReadFromJsonAsync<UserDto>(Ct))!.Role);
    }

    [Fact]
    public async Task A_cascade_tells_the_owners_but_not_the_admin_who_made_the_change()
    {
        var sam = await SamAsync();
        (await sam.PostAsJsonAsync("/api/bookings",
            new { resourceId = Seed.SevernId, start = "2026-11-30T14:00", end = "2026-11-30T15:00", attendees = 4 }, Ct)).EnsureSuccessStatusCode();

        (await sam.PostAsJsonAsync($"/api/resources/{Seed.SevernId}/retirement", new { lastDay = Tomorrow }, Ct)).EnsureSuccessStatusCode();

        await using var db = _app.Db();
        Assert.Equal([Seed.AnaId], await db.Notices.Select(n => n.UserId).ToListAsync(Ct));
    }

    public static TheoryData<string, string, string> Refusals => new()
    {
        { "PUT", "/api/users/{missing}", "No such user" },
        { "POST", "/api/users/{missing}/deactivation", "No such user" },
        { "PUT", "/api/resources/{missing}", "No such resource" },
        { "POST", "/api/resources/{missing}/retirement", "No such resource" },
        { "GET", "/api/users/{missing}", "No such user" },
        { "POST", "/api/resources/{severn}/retirement?year=9999", "Give a last day before the year 9999" },
        { "GET", "/api/history?entityType=Notice", "entityType is User, Resource or Booking" },
        { "GET", "/api/history?from=9999-12-31", "Give dates before the year 9999" },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task Unknown_ids_and_out_of_range_input_are_refused_with_a_message(string method, string url, string message)
    {
        var sam = await SamAsync();
        url = url.Replace("{missing}", Guid.CreateVersion7().ToString()).Replace("{severn}", Seed.SevernId.ToString());
        object body = url.Contains("/retirement", StringComparison.Ordinal)
            ? new { lastDay = url.EndsWith("year=9999", StringComparison.Ordinal) ? "9999-12-31" : Tomorrow }
            : url.Contains("/api/users", StringComparison.Ordinal)
                ? new { email = "x@sonicjoy-solutions.com", displayName = "X" }
                : new { name = "X", type = "MeetingRoom", capacity = 2 };

        var response = await sam.SendAsync(new HttpRequestMessage(new HttpMethod(method), url) { Content = method == "GET" ? null : JsonContent.Create(body) }, Ct);

        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.BadRequest });
        Assert.Contains(message, await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Users_list_by_name_and_read_one_by_id()
    {
        var sam = await SamAsync();

        var all = (await sam.GetFromJsonAsync<List<UserDto>>("/api/users", Ct))!;
        var ben = (await sam.GetFromJsonAsync<UserDto>($"/api/users/{Seed.BenId}", Ct))!;

        Assert.Equal(["Ana", "Ben", "Sam"], all.Select(u => u.DisplayName));
        Assert.Equal(("ben@sonicjoy-solutions.com", Role.Employee, true), (ben.Email, ben.Role, ben.IsActive));
    }
}
