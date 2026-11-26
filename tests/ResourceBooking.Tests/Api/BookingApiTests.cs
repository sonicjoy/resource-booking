using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ResourceBooking.Auth;
using ResourceBooking.Bookings;
using ResourceBooking.Data;
using ResourceBooking.Data.Entities;
using ResourceBooking.Shared;
using ResourceBooking.Shared.Dtos;
using ResourceBooking.Tests.Infrastructure;

namespace ResourceBooking.Tests.Api;

/// <summary>
/// The booking API against the seed (see the design's CUJ seed list). The clock is Wednesday
/// 25 November 2026, 09:00 in London; "tomorrow" is Thursday 26 November.
/// </summary>
public class BookingApiTests(DbFixture fixture) : IAsyncLifetime
{
    private const string Tomorrow = "2026-11-26";
    private ApiFactory _app = null!;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _app = await ApiFactory.CreateAsync(fixture);

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    private static object Room(Guid resourceId, string start, string end, int attendees = 6, object? extra = null) =>
        new { resourceId, start = $"{Tomorrow}T{start}", end = $"{Tomorrow}T{end}", attendees, title = "Team sync" };

    private static object Lunch(string time, int portions = 8, Guid? room = null) =>
        new { resourceId = Seed.MainKitchenId, deliveryAt = $"{Tomorrow}T{time}", attendees = portions, deliveryRoomId = room ?? Seed.ThamesId };

    private static async Task<JsonElement> Body(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;

    // ---- create ----

    [Fact]
    public async Task Booking_a_free_room_returns_201_with_its_location_and_local_times()
    {
        var ana = await _app.SignedInAsync(Seed.AnaId);

        var response = await ana.PostAsJsonAsync("/api/bookings", Room(Seed.ThamesId, "14:00", "15:00"), Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var booking = await response.Content.ReadFromJsonAsync<BookingDto>(Ct);
        Assert.Equal($"/api/bookings/{booking!.Id}", response.Headers.Location?.OriginalString);
        Assert.Equal(new DateTime(2026, 11, 26, 14, 0, 0), booking.Start);
        Assert.Equal("Thames", booking.ResourceName);
        Assert.Equal(BookingStatus.Confirmed, booking.Status);
    }

    [Fact]
    public async Task An_overlap_is_409_overlap_naming_the_resource()
    {
        var ana = await _app.SignedInAsync(Seed.AnaId);
        var ben = await _app.SignedInAsync(Seed.BenId);
        (await ana.PostAsJsonAsync("/api/bookings", Room(Seed.ThamesId, "14:00", "15:00"), Ct)).EnsureSuccessStatusCode();

        var response = await ben.PostAsJsonAsync("/api/bookings", Room(Seed.ThamesId, "14:30", "15:30"), Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await Body(response);
        Assert.Equal("OVERLAP", body.GetProperty("code").GetString());
        Assert.Equal("Thames is already booked then", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task A_clash_inside_a_cars_turnaround_gap_names_the_gap()
    {
        var ana = await _app.SignedInAsync(Seed.AnaId);

        var response = await ana.PostAsJsonAsync("/api/bookings", Room(Seed.PoolCar1Id, "11:30", "13:00", attendees: 2), Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("Pool Car 1 is booked then, or within its 60-minute gap between bookings",
            (await Body(response)).GetProperty("message").GetString());
    }

    [Fact]
    public async Task Rule_violations_are_422_listing_each_code_and_message()
    {
        var ana = await _app.SignedInAsync(Seed.AnaId);

        var response = await ana.PostAsJsonAsync("/api/bookings", Room(Seed.ThamesId, "15:00", "18:30", attendees: 12), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var violations = (await Body(response)).GetProperty("violations").EnumerateArray()
            .Select(v => (v.GetProperty("code").GetString(), v.GetProperty("message").GetString())).ToList();
        Assert.Contains(("OVER_CAPACITY", "Thames seats 8"), violations);
        Assert.Contains(("OUTSIDE_OPENING_HOURS", "Thames closes at 18:00"), violations);
    }

    [Theory]
    [InlineData("""{"resourceId":"0193a000-0000-7000-8000-000000000101","start":"2026-11-26T15:00","end":"2026-11-26T14:00","attendees":2}""")]
    [InlineData("""{"resourceId":"0193a000-0000-7000-8000-000000000101","start":"2026-11-26T14:00:00Z","end":"2026-11-26T15:00:00Z","attendees":2}""")]
    [InlineData("""{"resourceId":"0193a000-0000-7000-8000-000000000101","start":"2026-11-26T14:00","end":"2026-11-26T15:00","deliveryAt":"2026-11-26T14:00","attendees":2}""")]
    [InlineData("""{"resourceId":"0193a000-0000-7000-8000-000000000104","start":"2026-11-26T12:00","end":"2026-11-26T13:00","attendees":2}""")]
    [InlineData("""{"resourceId":"0193a000-0000-7000-8000-000000000101","start":"2026-11-26T14:00","end":"2026-11-26T15:00","attendees":0}""")]
    public async Task Malformed_or_misshapen_requests_are_400(string json)
    {
        var ana = await _app.SignedInAsync(Seed.AnaId);

        var response = await ana.PostAsync("/api/bookings", new StringContent(json, System.Text.Encoding.UTF8, "application/json"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_local_time_skipped_by_the_clock_change_is_400()
    {
        _app.Clock.SetUtcNow(new DateTimeOffset(2027, 3, 20, 9, 0, 0, TimeSpan.Zero));
        var ana = await _app.SignedInAsync(Seed.AnaId);

        // 28 March 2027: London clocks go from 01:00 to 02:00, so 01:30 does not exist.
        var response = await ana.PostAsJsonAsync("/api/bookings",
            new { resourceId = Seed.PoolCar1Id, start = "2027-03-28T01:30", end = "2027-03-28T09:00", attendees = 1 }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_unknown_resource_is_404()
    {
        var ana = await _app.SignedInAsync(Seed.AnaId);

        var response = await ana.PostAsJsonAsync("/api/bookings", Room(Guid.NewGuid(), "14:00", "15:00"), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Twenty_concurrent_bookings_of_one_slot_give_one_201_and_nineteen_409()
    {
        var clients = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => _app.SignedInAsync(Seed.AnaId)));

        var statuses = await Task.WhenAll(clients.Select(async c =>
            (await c.PostAsJsonAsync("/api/bookings", Room(Seed.SevernId, "14:00", "15:00"), Ct)).StatusCode));

        Assert.Equal(1, statuses.Count(s => s == HttpStatusCode.Created));
        Assert.Equal(19, statuses.Count(s => s == HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task Six_orders_for_a_five_unit_kitchen_give_five_201_and_one_kitchen_full()
    {
        var ana = await _app.SignedInAsync(Seed.AnaId);

        var statuses = new List<HttpStatusCode>();
        HttpResponseMessage last = null!;
        for (var i = 0; i < 6; i++)
        {
            last = await ana.PostAsJsonAsync("/api/bookings", Lunch("12:30"), Ct);
            statuses.Add(last.StatusCode);
        }

        Assert.Equal(5, statuses.Count(s => s == HttpStatusCode.Created));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, statuses[^1]);
        var violation = (await Body(last)).GetProperty("violations")[0];
        Assert.Equal("KITCHEN_FULL", violation.GetProperty("code").GetString());
        await using var db = _app.Db();
        var units = await db.Bookings.Where(b => b.ResourceId == Seed.MainKitchenId && b.StartsAt > _app.Clock.GetUtcNow())
            .Select(b => b.UnitNo).OrderBy(u => u).ToListAsync(Ct);
        Assert.Equal([0, 1, 2, 3, 4], units);
    }

    [Fact]
    public async Task A_unit_number_sent_by_the_client_is_ignored()
    {
        var ana = await _app.SignedInAsync(Seed.AnaId);
        var ben = await _app.SignedInAsync(Seed.BenId);
        (await ana.PostAsJsonAsync("/api/bookings", Room(Seed.ThamesId, "14:00", "15:00"), Ct)).EnsureSuccessStatusCode();

        var response = await ben.PostAsync("/api/bookings", new StringContent(
            $$"""{"resourceId":"{{Seed.ThamesId}}","start":"{{Tomorrow}}T14:00","end":"{{Tomorrow}}T15:00","attendees":2,"unitNo":1}""",
            System.Text.Encoding.UTF8, "application/json"), Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task A_catering_order_stores_its_delivery_time_and_room()
    {
        var ana = await _app.SignedInAsync(Seed.AnaId);

        var response = await ana.PostAsJsonAsync("/api/bookings", Lunch("12:30"), Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var order = await response.Content.ReadFromJsonAsync<BookingDto>(Ct);
        Assert.Equal(new DateTime(2026, 11, 26, 12, 30, 0), order!.DeliveryAt);
        Assert.Equal(Seed.ThamesId, order.DeliveryRoomId);
    }

    [Fact]
    public async Task A_catering_order_must_be_delivered_to_an_active_meeting_room()
    {
        var ana = await _app.SignedInAsync(Seed.AnaId);

        var toCar = await ana.PostAsJsonAsync("/api/bookings", Lunch("12:30", room: Seed.PoolCar1Id), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, toCar.StatusCode);
    }

    [Fact]
    public async Task Signed_out_booking_is_401()
    {
        var response = await _app.Client().PostAsJsonAsync("/api/bookings", Room(Seed.ThamesId, "14:00", "15:00"), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- read, edit, cancel ----

    private async Task<(HttpClient Ana, BookingDto Booking)> AnaBooksThamesAsync()
    {
        var ana = await _app.SignedInAsync(Seed.AnaId);
        var response = await ana.PostAsJsonAsync("/api/bookings", Room(Seed.ThamesId, "14:00", "15:00"), Ct);
        response.EnsureSuccessStatusCode();
        return (ana, (await response.Content.ReadFromJsonAsync<BookingDto>(Ct))!);
    }

    [Fact]
    public async Task My_bookings_lists_only_the_callers_bookings()
    {
        var (ana, booking) = await AnaBooksThamesAsync();

        var mine = await ana.GetFromJsonAsync<List<BookingDto>>($"/api/bookings/mine?from={Tomorrow}&to={Tomorrow}", Ct);

        Assert.Contains(mine!, b => b.Id == booking.Id);
        await using var db = _app.Db();
        var anaIds = await db.Bookings.Where(b => b.UserId == Seed.AnaId).Select(b => b.Id).ToListAsync(Ct);
        Assert.All(mine!, b => Assert.Contains(b.Id, anaIds));
    }

    [Fact]
    public async Task Someone_elses_booking_is_404_to_read_change_or_cancel()
    {
        var (_, booking) = await AnaBooksThamesAsync();
        var ben = await _app.SignedInAsync(Seed.BenId);

        Assert.Equal(HttpStatusCode.NotFound, (await ben.GetAsync($"/api/bookings/{booking.Id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ben.PutAsJsonAsync($"/api/bookings/{booking.Id}", Room(Seed.ThamesId, "15:00", "16:00"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ben.DeleteAsync($"/api/bookings/{booking.Id}", Ct)).StatusCode);
    }

    [Fact]
    public async Task Editing_moves_the_booking_and_keeps_its_id()
    {
        var (ana, booking) = await AnaBooksThamesAsync();

        var response = await ana.PutAsJsonAsync($"/api/bookings/{booking.Id}", Room(Seed.ThamesId, "15:00", "16:00", attendees: 4), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var moved = await response.Content.ReadFromJsonAsync<BookingDto>(Ct);
        Assert.Equal((booking.Id, new DateTime(2026, 11, 26, 15, 0, 0), 4), (moved!.Id, moved.Start, moved.Attendees));
    }

    [Fact]
    public async Task An_edit_that_overlaps_only_itself_is_allowed_and_one_at_the_cap_is_too()
    {
        var (ana, booking) = await AnaBooksThamesAsync();
        await using (var db = _app.Db())
        {
            await db.Resources.Where(r => r.Id == Seed.ThamesId).ExecuteUpdateAsync(r => r.SetProperty(x => x.MaxFuturePerUser, 1), Ct);
        }

        var response = await ana.PutAsJsonAsync($"/api/bookings/{booking.Id}", Room(Seed.ThamesId, "14:30", "15:30"), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task An_edit_cannot_move_a_booking_to_another_resource()
    {
        var (ana, booking) = await AnaBooksThamesAsync();

        var response = await ana.PutAsJsonAsync($"/api/bookings/{booking.Id}", Room(Seed.SevernId, "14:00", "15:00"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Cancelling_keeps_the_row_and_a_second_cancel_is_409_not_confirmed()
    {
        var (ana, booking) = await AnaBooksThamesAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await ana.DeleteAsync($"/api/bookings/{booking.Id}", Ct)).StatusCode);
        var again = await ana.DeleteAsync($"/api/bookings/{booking.Id}", Ct);

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("NOT_CONFIRMED", (await Body(again)).GetProperty("code").GetString());
        await using var db = _app.Db();
        Assert.Equal(BookingStatus.Cancelled, (await db.Bookings.SingleAsync(b => b.Id == booking.Id, Ct)).Status);
    }

    [Fact]
    public async Task A_booking_that_has_started_cannot_be_changed_or_cancelled()
    {
        var ana = await _app.SignedInAsync(Seed.AnaId);
        Guid pastId;
        await using (var db = _app.Db())
        {
            pastId = await db.Bookings.Where(b => b.UserId == Seed.AnaId && b.EndsAt < _app.Clock.GetUtcNow()).Select(b => b.Id).FirstAsync(Ct);
        }

        var response = await ana.DeleteAsync($"/api/bookings/{pastId}", Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("ALREADY_STARTED", (await Body(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Every_booking_write_leaves_its_audit_event()
    {
        var (ana, booking) = await AnaBooksThamesAsync();
        await ana.PutAsJsonAsync($"/api/bookings/{booking.Id}", Room(Seed.ThamesId, "15:00", "16:00"), Ct);
        await ana.DeleteAsync($"/api/bookings/{booking.Id}", Ct);

        await using var db = _app.Db();
        var events = await db.AuditEvents.Where(e => e.EntityId == booking.Id).OrderBy(e => e.Id).ToListAsync(Ct);
        Assert.Equal(["Added", "Modified", "Modified"], events.Select(e => e.Action));
        Assert.All(events, e => Assert.Equal(Seed.AnaId, e.ActorId));
    }

    // ---- availability and free resources ----

    [Fact]
    public async Task Availability_hides_other_peoples_owner_and_title_from_employees()
    {
        var ana = await _app.SignedInAsync(Seed.AnaId);
        var sam = await _app.SignedInAsync(Seed.SamId);

        var asAna = await ana.GetFromJsonAsync<AvailabilityDay>($"/api/availability?date={Tomorrow}&type=Car", Ct);
        var asSam = await sam.GetFromJsonAsync<AvailabilityDay>($"/api/availability?date={Tomorrow}&type=Car", Ct);

        var carForAna = Assert.Single(asAna!.Resources);
        var block = Assert.Single(carForAna.Busy);
        Assert.Equal((new DateTime(2026, 11, 26, 9, 0, 0), new DateTime(2026, 11, 26, 11, 0, 0)), (block.Start, block.End));
        Assert.Equal((false, null, null), (block.Mine, block.Title, block.Owner));
        var forSam = Assert.Single(Assert.Single(asSam!.Resources).Busy);
        Assert.Equal(("Ben", "Site visit"), (forSam.Owner, forSam.Title));
    }

    [Fact]
    public async Task Availability_shows_the_callers_own_bookings_and_kitchen_units()
    {
        var (ana, _) = await AnaBooksThamesAsync();
        await ana.PostAsJsonAsync("/api/bookings", Lunch("12:30"), Ct);

        var rooms = await ana.GetFromJsonAsync<AvailabilityDay>($"/api/availability?date={Tomorrow}&type=MeetingRoom", Ct);
        var kitchens = await ana.GetFromJsonAsync<AvailabilityDay>($"/api/availability?date={Tomorrow}&type=Catering", Ct);

        var mine = Assert.Single(rooms!.Resources.Single(r => r.Name == "Thames").Busy);
        Assert.Equal((true, "Team sync"), (mine.Mine, mine.Title));
        var kitchen = Assert.Single(kitchens!.Resources);
        Assert.Equal((5, 4), (kitchen.Units, kitchen.FreeUnits));
    }

    [Fact]
    public async Task Free_resources_respect_overlaps_capacity_and_the_turnaround_gap()
    {
        var (ana, _) = await AnaBooksThamesAsync();

        var rooms = await ana.GetFromJsonAsync<List<ResourceDto>>(
            $"/api/resources/free?type=MeetingRoom&start={Tomorrow}T14:00&end={Tomorrow}T15:00&attendees=6", Ct);
        var cars = await ana.GetFromJsonAsync<List<ResourceDto>>(
            $"/api/resources/free?type=Car&start={Tomorrow}T11:30&end={Tomorrow}T13:00&attendees=2", Ct);

        Assert.Equal(["Severn"], rooms!.Select(r => r.Name));
        Assert.Empty(cars!);
    }

    [Fact]
    public async Task Resources_list_the_active_ones_with_their_rules()
    {
        var ana = await _app.SignedInAsync(Seed.AnaId);

        var resources = await ana.GetFromJsonAsync<List<ResourceDto>>("/api/resources", Ct);
        var thames = await ana.GetFromJsonAsync<ResourceDto>($"/api/resources/{Seed.ThamesId}", Ct);

        Assert.Equal(["Main Kitchen", "Pool Car 1", "Severn", "Thames"], resources!.Select(r => r.Name).Order());
        Assert.Equal((8, TimeSpan.FromHours(4)), (thames!.Capacity, thames.MaxDuration));
    }

    // ---- proposals ----

    private async Task<Guid> ProposalAsync(Guid owner, ProposalKind kind = ProposalKind.Create, ProposalStatus status = ProposalStatus.Open,
        TimeSpan? age = null, int attendees = 6)
    {
        var id = Guid.CreateVersion7();
        await using var db = _app.Db();
        db.Proposals.Add(new Proposal
        {
            Id = id,
            ConversationId = Guid.CreateVersion7(),
            UserId = owner,
            Turn = 1,
            Kind = kind,
            Payload = JsonSerializer.Serialize(new { resourceId = Seed.ThamesId, start = $"{Tomorrow}T14:00", end = $"{Tomorrow}T15:00", attendees }),
            Status = status,
            CreatedAt = _app.Clock.GetUtcNow() - (age ?? TimeSpan.FromMinutes(5)),
        });
        await db.SaveChangesAsync(Ct);
        return id;
    }

    private async Task<(Booking Booking, Proposal Proposal)> BookWithProposalAsync(Guid proposalId, int attendees = 6)
    {
        var ana = await _app.SignedInAsync(Seed.AnaId);
        var request = new { resourceId = Seed.ThamesId, start = $"{Tomorrow}T14:00", end = $"{Tomorrow}T15:00", attendees, proposalId };
        var response = await ana.PostAsJsonAsync("/api/bookings", request, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await response.Content.ReadFromJsonAsync<BookingDto>(Ct))!.Id;
        await using var db = _app.Db();
        return (await db.Bookings.SingleAsync(b => b.Id == id, Ct), await db.Proposals.SingleAsync(p => p.Id == proposalId, Ct));
    }

    [Fact]
    public async Task Confirming_your_open_proposal_links_it()
    {
        var (booking, proposal) = await BookWithProposalAsync(await ProposalAsync(Seed.AnaId));

        Assert.Equal(proposal.Id, booking.ProposalId);
        Assert.Equal((ProposalStatus.Confirmed, false), (proposal.Status, proposal.ChangedBeforeConfirm));
    }

    [Fact]
    public async Task Changing_the_form_before_confirming_is_recorded()
    {
        var (_, proposal) = await BookWithProposalAsync(await ProposalAsync(Seed.AnaId), attendees: 7);

        Assert.True(proposal.ChangedBeforeConfirm);
    }

    public static TheoryData<string> Unlinkable => ["someone else's", "superseded", "expired", "wrong kind"];

    [Theory]
    [MemberData(nameof(Unlinkable))]
    public async Task A_proposal_that_does_not_qualify_never_stops_the_booking(string which)
    {
        var proposalId = which switch
        {
            "someone else's" => await ProposalAsync(Seed.BenId),
            "superseded" => await ProposalAsync(Seed.AnaId, status: ProposalStatus.Superseded),
            "expired" => await ProposalAsync(Seed.AnaId, age: TimeSpan.FromHours(25)),
            _ => await ProposalAsync(Seed.AnaId, kind: ProposalKind.Cancel),
        };

        var (booking, proposal) = await BookWithProposalAsync(proposalId);

        Assert.Null(booking.ProposalId);
        Assert.NotEqual(ProposalStatus.Confirmed, proposal.Status);
    }

    // ---- notices and cascades ----

    /// <summary>Runs a cascade as the signed-in admin would: inside a request scope with Sam as the user.</summary>
    private async Task CancelBensFutureBookingsAsSamAsync()
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var sam = new User { Id = Seed.SamId, Email = "sam@sonicjoy-solutions.com", DisplayName = "Sam", Role = Role.Admin, IsActive = true };
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext =
            new DefaultHttpContext { User = CookieSetup.PrincipalFor(sam), RequestServices = scope.ServiceProvider };
        var cancelled = await scope.ServiceProvider.GetRequiredService<BookingService>()
            .CancelFutureBookingsAsync(b => b.UserId == Seed.BenId, "Leaver", Seed.SamId, Ct);
        Assert.Equal(2, cancelled);
    }

    [Fact]
    public async Task Cancelling_future_bookings_notifies_active_owners_and_audits_each_change()
    {
        await CancelBensFutureBookingsAsSamAsync();

        var ben = await _app.SignedInAsync(Seed.BenId);
        var notices = await ben.GetFromJsonAsync<List<NoticeDto>>("/api/notices", Ct);
        Assert.Equal(2, notices!.Count);
        Assert.All(notices, n => Assert.False(n.Read));

        Assert.Equal(HttpStatusCode.NoContent, (await ben.PostAsync($"/api/notices/{notices[0].Id}/read", null, Ct)).StatusCode);
        Assert.True((await ben.GetFromJsonAsync<List<NoticeDto>>("/api/notices", Ct))!.Single(n => n.Id == notices[0].Id).Read);

        await using var db = _app.Db();
        var benBookings = await db.Bookings.Where(b => b.UserId == Seed.BenId && b.StartsAt > _app.Clock.GetUtcNow()).ToListAsync(Ct);
        Assert.All(benBookings, b => Assert.Equal((BookingStatus.Cancelled, "Leaver"), (b.Status, b.CancelReason)));
        var cancelEvents = await db.AuditEvents.Where(e => e.Action == "Modified" && benBookings.Select(b => b.Id).Contains(e.EntityId)).ToListAsync(Ct);
        Assert.Equal(2, cancelEvents.Count);
        Assert.All(cancelEvents, e => Assert.Equal(Seed.SamId, e.ActorId));
    }

    [Fact]
    public async Task Someone_elses_notice_cannot_be_marked_read()
    {
        await CancelBensFutureBookingsAsSamAsync();
        await using var db = _app.Db();
        var benNotice = await db.Notices.FirstAsync(n => n.UserId == Seed.BenId, Ct);
        var ana = await _app.SignedInAsync(Seed.AnaId);

        Assert.Equal(HttpStatusCode.NotFound, (await ana.PostAsync($"/api/notices/{benNotice.Id}/read", null, Ct)).StatusCode);
    }
}
