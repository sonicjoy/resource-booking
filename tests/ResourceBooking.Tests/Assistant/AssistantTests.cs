using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CsCheck;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using ResourceBooking.Data;
using ResourceBooking.Data.Entities;
using ResourceBooking.Shared;
using ResourceBooking.Shared.Dtos;
using ResourceBooking.Tests.Infrastructure;
using static ResourceBooking.Tests.Infrastructure.StubChatClient;

namespace ResourceBooking.Tests.Assistant;

/// <summary>
/// The assistant against a scripted model (design: "Assistant"). The clock is Wednesday 25 November 2026,
/// 09:00 in London; "tomorrow" is Thursday 26 November.
/// </summary>
public class AssistantTests(DbFixture fixture) : IAsyncLifetime
{
    private const string Tomorrow = "2026-11-26";
    private ApiFactory _app = null!;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _app = await ApiFactory.CreateAsync(fixture);

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    private Task<HttpResponseMessage> SayAsync(HttpClient client, string text, Guid? conversationId = null) =>
        client.PostAsJsonAsync("/api/assistant/messages", new { conversationId, text }, Ct);

    private static async Task<AssistantReply> ReplyAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AssistantReply>(Ct))!;
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.TryGetProperty("code", out var c) ? c.GetString() : null;

    private static ToolCall ProposeRoom(Guid room, string start, string end, int attendees, object? extra = null) =>
        Call("propose_booking", new { resourceId = room, start = $"{Tomorrow}T{start}", end = $"{Tomorrow}T{end}", attendees, userId = extra });

    // ---- proposals ----

    [Fact]
    public async Task A_proposal_is_stored_for_the_caller_and_books_nothing()
    {
        _app.Model.On("a room", "Thames is free from 14:00.", ProposeRoom(Seed.ThamesId, "14:00", "15:00", 6, extra: Seed.BenId));
        var ana = await _app.SignedInAsync(Seed.AnaId);
        await using var before = _app.Db();
        var bookings = await before.Bookings.CountAsync(Ct);

        var reply = await ReplyAsync(await SayAsync(ana, "a room for 6 tomorrow afternoon"));

        var proposal = Assert.Single(reply.Proposals);
        Assert.Equal((ProposalKind.Create, Seed.ThamesId, 6), (proposal.Kind, proposal.Request!.ResourceId!.Value, proposal.Request.Attendees));
        Assert.Equal("Thames is free from 14:00.", reply.Text);
        await using var db = _app.Db();
        Assert.Equal(Seed.AnaId, (await db.Proposals.SingleAsync(Ct)).UserId);
        Assert.Equal(bookings, await db.Bookings.CountAsync(Ct));
    }

    [Fact]
    public async Task Tools_see_only_the_callers_bookings()
    {
        _app.Model.On("my bookings", "You have one.", Call("get_my_bookings", new { }));
        var ana = await _app.SignedInAsync(Seed.AnaId);

        await ReplyAsync(await SayAsync(ana, "what are my bookings?"));

        await using var db = _app.Db();
        var tool = await db.AssistantSteps.SingleAsync(s => s.Kind == StepKind.ToolCall, Ct);
        Assert.Contains("Quarterly review", tool.Payload);
        Assert.DoesNotContain("Site visit", tool.Payload);
    }

    public static TheoryData<string, object, string> Refused => new()
    {
        { "a busy slot", new { resourceId = Seed.PoolCar1Id, start = $"{Tomorrow}T09:30", end = $"{Tomorrow}T10:30", attendees = 2 }, "booked then" },
        { "a broken rule", new { resourceId = Seed.ThamesId, start = $"{Tomorrow}T10:00", end = $"{Tomorrow}T11:00", attendees = 12 }, "Thames seats 8" },
        { "no attendees", new { resourceId = Seed.ThamesId, start = $"{Tomorrow}T10:00", end = $"{Tomorrow}T11:00", attendees = 0 }, "Attendees" },
        { "a kitchen without a room", new { resourceId = Seed.MainKitchenId, deliveryAt = $"{Tomorrow}T12:30", attendees = 4 }, "delivery time and a room" },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task A_request_that_would_not_book_is_not_proposed_and_the_model_is_told_why(string id, object arguments, string reason)
    {
        _app.Model.On("book", "Sorry.", Call("propose_booking", arguments));
        var ana = await _app.SignedInAsync(Seed.AnaId);

        var reply = await ReplyAsync(await SayAsync(ana, $"book {id}"));

        Assert.Empty(reply.Proposals);
        await using var db = _app.Db();
        Assert.False(await db.Proposals.AnyAsync(Ct));
        Assert.Contains(reason, (await db.AssistantSteps.SingleAsync(s => s.Kind == StepKind.ToolCall, Ct)).Payload);
    }

    [Fact]
    public async Task Change_and_cancellation_proposals_name_the_callers_booking()
    {
        Guid severn;
        await using (var db = _app.Db())
        {
            severn = await db.Bookings.Where(b => b.UserId == Seed.AnaId && b.ResourceId == Seed.SevernId).Select(b => b.Id).SingleAsync(Ct);
        }
        _app.Model.On("move", "Moved.", Call("propose_change", new { bookingId = severn, start = "2026-11-28T14:00", end = "2026-11-28T15:00", attendees = 6 }));
        _app.Model.On("cancel", "Cancelled.", Call("propose_cancellation", new { bookingId = severn }));
        var ana = await _app.SignedInAsync(Seed.AnaId);

        var change = await ReplyAsync(await SayAsync(ana, "move my Severn booking to 14:00"));
        var cancel = await ReplyAsync(await SayAsync(ana, "cancel it", change.ConversationId));

        Assert.Equal((ProposalKind.Change, severn), (change.Proposals.Single().Kind, change.Proposals.Single().BookingId!.Value));
        Assert.Equal((ProposalKind.Cancel, severn), (cancel.Proposals.Single().Kind, cancel.Proposals.Single().BookingId!.Value));
        Assert.StartsWith("Cancel Severn, ", cancel.Proposals.Single().Summary);
    }

    [Fact]
    public async Task Someone_elses_booking_cannot_be_proposed_for_change_or_cancellation()
    {
        Guid bens;
        await using (var db = _app.Db())
        {
            bens = await db.Bookings.Where(b => b.UserId == Seed.BenId && b.ResourceId == Seed.PoolCar1Id && b.StartsAt > _app.Clock.GetUtcNow()).Select(b => b.Id).SingleAsync(Ct);
        }
        _app.Model.On("cancel", "Sorry.", Call("propose_cancellation", new { bookingId = bens }));
        var ana = await _app.SignedInAsync(Seed.AnaId);

        var reply = await ReplyAsync(await SayAsync(ana, "cancel the car booking"));

        Assert.Empty(reply.Proposals);
    }

    [Fact]
    public async Task A_new_turn_supersedes_the_last_turns_proposals_and_keeps_its_own_together()
    {
        _app.Model.On("for 6", "Two options.", ProposeRoom(Seed.ThamesId, "14:00", "15:00", 6), ProposeRoom(Seed.SevernId, "14:00", "15:00", 6));
        _app.Model.On("for 10", "Severn then.", ProposeRoom(Seed.SevernId, "14:00", "15:00", 10));
        var ana = await _app.SignedInAsync(Seed.AnaId);

        var first = await ReplyAsync(await SayAsync(ana, "a room for 6 tomorrow at 14:00"));
        Assert.Equal(2, first.Proposals.Count);
        var second = await ReplyAsync(await SayAsync(ana, "make it for 10", first.ConversationId));

        Assert.Equal(10, second.Proposals.Single().Request!.Attendees);
        await using var db = _app.Db();
        var statuses = await db.Proposals.OrderBy(p => p.Turn).Select(p => new { p.Turn, p.Status }).ToListAsync(Ct);
        Assert.Equal([(1, ProposalStatus.Superseded), (1, ProposalStatus.Superseded), (2, ProposalStatus.Open)], statuses.Select(s => (s.Turn, s.Status)));
    }

    [Fact]
    public async Task Confirming_a_proposal_through_the_form_books_it_and_links_it()
    {
        _app.Model.On("a room", "Here it is.", ProposeRoom(Seed.ThamesId, "14:00", "15:00", 6));
        var ana = await _app.SignedInAsync(Seed.AnaId);
        var proposal = (await ReplyAsync(await SayAsync(ana, "a room please"))).Proposals.Single();
        proposal.Request!.ProposalId = proposal.Id;

        var booked = await ana.PostAsJsonAsync("/api/bookings", proposal.Request, Ct);

        Assert.Equal(HttpStatusCode.Created, booked.StatusCode);
        await using var db = _app.Db();
        Assert.Equal(proposal.Id, (await db.Bookings.SingleAsync(b => b.ProposalId != null, Ct)).ProposalId);
        Assert.Equal(ProposalStatus.Confirmed, (await db.Proposals.SingleAsync(Ct)).Status);
    }

    // ---- conversations ----

    [Fact]
    public async Task Someone_elses_or_an_unknown_conversation_is_404()
    {
        var ana = await _app.SignedInAsync(Seed.AnaId);
        var ben = await _app.SignedInAsync(Seed.BenId);
        var anas = await ReplyAsync(await SayAsync(ana, "hello"));

        Assert.Equal(HttpStatusCode.NotFound, (await SayAsync(ben, "hello", anas.ConversationId)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SayAsync(ana, "hello", Guid.CreateVersion7())).StatusCode);
    }

    [Fact]
    public async Task A_conversation_holds_at_most_20_turns()
    {
        var ana = await _app.SignedInAsync(Seed.AnaId);
        var conversation = (await ReplyAsync(await SayAsync(ana, "hello"))).ConversationId;
        await using (var db = _app.Db())
        {
            var seq = await db.AssistantSteps.Where(s => s.ConversationId == conversation).MaxAsync(s => s.Seq, Ct);
            db.AssistantSteps.AddRange(Enumerable.Range(1, 19).Select(i => new AssistantStep
            {
                Id = Guid.CreateVersion7(),
                ConversationId = conversation,
                UserId = Seed.AnaId,
                Seq = seq + i,
                Kind = StepKind.UserMessage,
                Payload = "\"again\"",
                At = _app.Clock.GetUtcNow(),
            }));
            await db.SaveChangesAsync(Ct);
        }

        var response = await SayAsync(ana, "one more", conversation);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("TURN_LIMIT", await CodeAsync(response));
    }

    [Fact]
    public async Task The_model_gets_todays_date_the_users_name_and_the_conversation_so_far()
    {
        _app.Model.On("hello", "Hi Ana.");
        var ana = await _app.SignedInAsync(Seed.AnaId);

        var first = await ReplyAsync(await SayAsync(ana, "hello"));
        await ReplyAsync(await SayAsync(ana, "and again", first.ConversationId));

        var system = _app.Model.Calls[0].First(m => m.Role == ChatRole.System).Text;
        Assert.Contains("Wednesday 25 November 2026", system);
        Assert.Contains("Ana", system);
        Assert.Equal(["hello", "Hi Ana.", "and again"], _app.Model.Calls[1].Where(m => m.Role != ChatRole.System).Select(m => m.Text));
    }

    // ---- traces ----

    [Fact]
    public async Task Each_step_kind_is_traced_in_order_with_tokens_and_durations()
    {
        _app.Model.On("a room", "Done.", ProposeRoom(Seed.ThamesId, "14:00", "15:00", 6));
        var ana = await _app.SignedInAsync(Seed.AnaId);

        await ReplyAsync(await SayAsync(ana, "a room please"));

        await using var db = _app.Db();
        var steps = await db.AssistantSteps.OrderBy(s => s.Seq).ToListAsync(Ct);
        Assert.Equal([StepKind.UserMessage, StepKind.ModelCall, StepKind.ToolCall, StepKind.ModelCall, StepKind.Reply], steps.Select(s => s.Kind));
        Assert.Equal([1, 2, 3, 4, 5], steps.Select(s => s.Seq));
        Assert.All(steps.Where(s => s.Kind == StepKind.ModelCall), s => Assert.Equal((50, 50), (s.InputTokens, s.OutputTokens)));
        Assert.All(steps.Where(s => s.Kind is StepKind.ModelCall or StepKind.ToolCall), s => Assert.NotNull(s.DurationMs));
        Assert.Contains("propose_booking", steps[2].Payload);
    }

    [Fact]
    public async Task Admins_list_and_read_traces_and_employees_cannot()
    {
        _app.Model.On("a room", "Done.", ProposeRoom(Seed.ThamesId, "14:00", "15:00", 6));
        var ana = await _app.SignedInAsync(Seed.AnaId);
        var conversation = (await ReplyAsync(await SayAsync(ana, "a room please"))).ConversationId;
        var sam = await _app.SignedInAsync(Seed.SamId);

        var list = (await sam.GetFromJsonAsync<List<TraceSummary>>("/api/assistant/traces?from=2026-11-25&to=2026-11-25", Ct))!;
        var steps = (await sam.GetFromJsonAsync<List<TraceStep>>($"/api/assistant/traces/{conversation}", Ct))!;

        Assert.Equal(("Ana", 1, 5, 200), (list.Single().User, list.Single().Turns, list.Single().Steps, list.Single().Tokens));
        Assert.Equal(5, steps.Count);
        Assert.Equal(HttpStatusCode.Forbidden, (await ana.GetAsync("/api/assistant/traces", Ct)).StatusCode);
    }

    // ---- limits and failures ----

    [Fact]
    public async Task Every_model_call_counts_against_the_daily_budget_and_an_exhausted_budget_is_429()
    {
        _app.Model.On("a room", "Done.", ProposeRoom(Seed.ThamesId, "14:00", "15:00", 6));
        var ana = await _app.SignedInAsync(Seed.AnaId);
        await ReplyAsync(await SayAsync(ana, "a room please"));
        await using (var db = _app.Db())
        {
            Assert.Equal(200, (await db.TokenUsage.SingleAsync(Ct)).Tokens);
            (await db.TokenUsage.SingleAsync(Ct)).Tokens = 200_000;
            await db.SaveChangesAsync(Ct);
        }

        var response = await SayAsync(ana, "another room");

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("BUDGET_EXHAUSTED", await CodeAsync(response));
        Assert.Equal(2, _app.Model.Calls.Count);
    }

    [Fact]
    public async Task More_than_10_messages_a_minute_is_429()
    {
        var ana = await _app.SignedInAsync(Seed.AnaId);
        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await SayAsync(ana, "hello")).StatusCode);
        }

        var response = await SayAsync(ana, "hello");

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("RATE_LIMITED", await CodeAsync(response));
        Assert.Equal(HttpStatusCode.OK, (await SayAsync(await _app.SignedInAsync(Seed.BenId), "hello")).StatusCode);
    }

    [Fact]
    public async Task A_model_failure_gives_a_short_reply_and_is_traced()
    {
        _app.Model.Failure = new HttpRequestException("model down");
        var ana = await _app.SignedInAsync(Seed.AnaId);

        var reply = await ReplyAsync(await SayAsync(ana, "a room please"));

        Assert.Contains("couldn't answer", reply.Text);
        await using var db = _app.Db();
        Assert.Contains(await db.AssistantSteps.Select(s => s.Kind).ToListAsync(Ct), k => k == StepKind.Reply);
    }

    [Fact]
    public async Task A_slow_model_is_given_up_on_after_the_timeout()
    {
        await using var app = await ApiFactory.CreateAsync(fixture, settings: new Dictionary<string, string> { ["Assistant:TimeoutSeconds"] = "1" });
        app.Model.Delay = TimeSpan.FromSeconds(5);
        var ana = await app.SignedInAsync(Seed.AnaId);
        var started = DateTime.UtcNow;

        var reply = await ReplyAsync(await ana.PostAsJsonAsync("/api/assistant/messages", new { text = "hello" }, Ct));

        Assert.Contains("couldn't answer", reply.Text);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(4));
    }

    // ---- level 3: a proposal is offered exactly when the same request would book ----

    [Fact]
    public async Task A_proposal_is_offered_exactly_when_booking_the_same_request_would_succeed()
    {
        await using var app = await ApiFactory.CreateAsync(fixture, settings: new Dictionary<string, string> { ["Assistant:MessagesPerMinute"] = "0" });
        var ana = await app.SignedInAsync(Seed.AnaId);
        var resources = new[] { Seed.ThamesId, Seed.SevernId, Seed.PoolCar1Id };
        static string At(int minutes) => $"{Tomorrow}T{minutes / 60:00}:{minutes % 60:00}";
        var gen = Gen.Select(Gen.Int[0, 2], Gen.Int[14, 37], Gen.Int[1, 8], Gen.OneOf(Gen.Int[0, 2], Gen.Int[3, 14]))
            .Select((r, startHalfHour, halfHours, attendees) => new
            {
                resourceId = resources[r],
                start = At(startHalfHour * 30),
                end = At(Math.Min((startHalfHour + halfHours) * 30, 23 * 60 + 30)),
                attendees,
            });

        await gen.SampleAsync(async request =>
        {
            app.Model.On("property", "ok", Call("propose_booking", request));
            var reply = await ReplyAsync(await ana.PostAsJsonAsync("/api/assistant/messages", new { text = "property check" }, Ct));

            var booked = await ana.PostAsJsonAsync("/api/bookings", request, Ct);
            Assert.Equal(booked.StatusCode == HttpStatusCode.Created, reply.Proposals.Count == 1);
            if (booked.IsSuccessStatusCode)
            {
                var id = (await booked.Content.ReadFromJsonAsync<BookingDto>(Ct))!.Id;
                (await ana.DeleteAsync($"/api/bookings/{id}", Ct)).EnsureSuccessStatusCode();
            }
        }, iter: 40, threads: 1);
    }
}
