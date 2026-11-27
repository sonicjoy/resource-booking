using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using ResourceBooking.Data;
using ResourceBooking.Shared.Dtos;
using static Microsoft.Playwright.Assertions;
using static ResourceBooking.Tests.Infrastructure.StubChatClient;

namespace ResourceBooking.E2E;

/// <summary>
/// CUJ 6: Ana asks the assistant for a room and lunch, changes her mind, and confirms both proposals in the
/// booking form; Sam reads the conversation's trace. The model is the scripted stub, so this needs the local app.
/// </summary>
[Collection(E2ECollection.Name)]
public class Cuj6AssistantTests(AppFixture app) : IAsyncLifetime
{
    public async ValueTask InitializeAsync()
    {
        if (!app.Remote)
        {
            await app.ReseedAsync();
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static string Tomorrow => Pages.LondonToday().AddDays(1).ToString("yyyy-MM-dd");

    private static ToolCall Room(Guid room, int people) =>
        Call("propose_booking", new { resourceId = room, start = $"{Tomorrow}T09:00", end = $"{Tomorrow}T12:00", attendees = people });

    private static ToolCall Lunch(Guid room, int portions) =>
        Call("propose_booking", new { resourceId = Seed.MainKitchenId, deliveryAt = $"{Tomorrow}T12:30", deliveryRoomId = room, attendees = portions });

    [Fact]
    public async Task Ana_books_a_room_and_lunch_through_the_assistant_and_Sam_reads_the_trace()
    {
        Assert.SkipWhen(app.Remote, "The deployed app's model can't be scripted.");
        app.Model.On("a room for 8", "Thames is free tomorrow morning, and lunch can go there at 12:30.", Room(Seed.ThamesId, 8), Lunch(Seed.ThamesId, 8));
        app.Model.On("make it 10", "Thames seats 8, so Severn instead, with lunch for 10.", Room(Seed.SevernId, 10), Lunch(Seed.SevernId, 10));

        var ana = await app.SignInAsync("Ana");
        await ana.Button("Ask assistant").ClickAsync();
        var panel = ana.Assistant();
        await panel.FillInAsync("Message", "a room for 8 tomorrow morning, and lunch for them at 12:30");
        await panel.Button("Send").ClickAsync();

        await Expect(panel.GetByText("Thames is free tomorrow morning")).ToBeVisibleAsync();
        var proposals = panel.Locator(".proposal");
        await Expect(proposals).ToHaveCountAsync(2);
        await Expect(proposals.Nth(0)).ToContainTextAsync("Thames");
        await Expect(proposals.Nth(1)).ToContainTextAsync("Main Kitchen: 8 portions to Thames");

        // Reviewing opens the form pre-filled; closing it books nothing.
        await proposals.Nth(0).Button("Review").ClickAsync();
        await Expect(ana).ToHaveURLAsync(new Regex($"/book\\?date={Tomorrow}&type=MeetingRoom$"));
        await Expect(ana.Form().GetByLabel("Start", new() { Exact = true })).ToHaveValueAsync("09:00");
        await Expect(ana.Form().GetByLabel("End", new() { Exact = true })).ToHaveValueAsync("12:00");
        await ana.Form().Button("Cancel").ClickAsync();
        await Expect(ana.Cell("Thames", "09:00")).ToHaveAttributeAsync("aria-label", "Thames, 09:00, free");

        await panel.FillInAsync("Message", "make it 10 people");
        await panel.Button("Send").ClickAsync();
        await Expect(panel.GetByText("Severn instead")).ToBeVisibleAsync();
        await Expect(proposals).ToHaveCountAsync(2);
        await Expect(proposals.Nth(0)).ToContainTextAsync("Severn");
        await Expect(proposals.Nth(1)).ToContainTextAsync("Main Kitchen: 10 portions to Severn");

        await proposals.Nth(0).Button("Review").ClickAsync();
        await Expect(ana.Form().GetByLabel("Start", new() { Exact = true })).ToHaveValueAsync("09:00");
        await ana.ConfirmAsync();
        await Expect(ana.Locator("[role=status]")).ToContainTextAsync("Booked Severn");
        await Expect(proposals.Nth(0)).ToContainTextAsync("Confirmed");

        await proposals.Nth(1).Button("Review").ClickAsync();
        await Expect(ana.Form().GetByLabel("Delivery time")).ToHaveValueAsync("12:30");
        await Expect(ana.Form().GetByLabel("Delivery room")).ToHaveValueAsync(Seed.SevernId.ToString());
        await ana.ConfirmAsync();
        await Expect(ana.Locator("[role=status]")).ToContainTextAsync("Ordered from Main Kitchen: 10 portions");
        await Expect(proposals.Nth(1)).ToContainTextAsync("Confirmed");

        // The grid shows tomorrow, where the booking now is.
        await Expect(ana.Cell("Severn", "09:00")).ToHaveAttributeAsync("aria-label", new Regex("your booking"));

        var sam = await app.SignInAsync("Sam");
        await sam.GoToAsync("Traces");
        await sam.Row("Ana").Button("Open").ClickAsync();
        var timeline = sam.Locator(".timeline");
        await Expect(timeline.Locator(".step")).ToHaveCountAsync(12);
        await Expect(timeline).ToContainTextAsync("a room for 8 tomorrow morning");
        await Expect(timeline).ToContainTextAsync("propose_booking");
        await Expect(timeline).ToContainTextAsync("Severn instead");
    }

    [Fact]
    public async Task Each_message_shows_its_own_text_without_leading_space_and_keeps_its_line_breaks()
    {
        Assert.SkipWhen(app.Remote, "The deployed app's model can't be scripted.");
        app.Model.On("two lines please", "Line one\nLine two");

        var ana = await app.SignInAsync("Ana");
        await ana.Button("Ask assistant").ClickAsync();
        var panel = ana.Assistant();
        await panel.FillInAsync("Message", "two lines please");
        await panel.Button("Send").ClickAsync();
        await Expect(panel.GetByText("Line two")).ToBeVisibleAsync();

        Assert.Equal("two lines please", await ShownTextAsync(panel.Locator(".message.mine").Last));
        Assert.Equal("Line one\nLine two", await ShownTextAsync(panel.Locator(".message.theirs").Last));
    }

    // The text as rendered (innerText honours white-space), leaving out the screen-reader label.
    private static Task<string> ShownTextAsync(ILocator message) => message.EvaluateAsync<string>("""
        li => {
            const label = li.querySelector('.visually-hidden');
            label.style.display = 'none';
            const text = li.innerText;
            label.style.display = '';
            return text;
        }
        """);

    [Fact]
    public async Task On_a_phone_the_panel_opens_full_screen_and_closes_back_to_the_grid()
    {
        var ana = await app.SignInAsync("Ana");
        await ana.SetViewportSizeAsync(375, 812);

        await ana.Button("Ask assistant").ClickAsync();
        var box = await ana.Assistant().BoundingBoxAsync();

        Assert.NotNull(box);
        Assert.Equal((0f, 0f, 375f, 812f), (box.X, box.Y, MathF.Round(box.Width), MathF.Round(box.Height)));
        await Expect(ana.Assistant().GetByLabel("Message")).ToBeInViewportAsync();
        await ana.Assistant().Button("Close").ClickAsync();
        await Expect(ana.Assistant()).ToBeHiddenAsync();
        await Expect(ana.Cell("Thames", "17:30")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Ana_cancels_a_booking_through_the_assistant()
    {
        Assert.SkipWhen(app.Remote, "The deployed app's model can't be scripted.");
        using var api = await app.ApiAsAsync(Seed.AnaId);
        var mine = await api.GetFromJsonAsync<List<BookingDto>>("/api/bookings/mine", TestContext.Current.CancellationToken);
        var id = mine!.Single(b => b.ResourceName == "Severn").Id;
        app.Model.On("cancel my Severn", "Here is the cancellation to confirm.", Call("propose_cancellation", new { bookingId = id }));

        var ana = await app.SignInAsync("Ana");
        await ana.Button("Ask assistant").ClickAsync();
        var panel = ana.Assistant();
        await panel.FillInAsync("Message", "cancel my Severn booking");
        await panel.Button("Send").ClickAsync();

        var card = panel.Locator(".proposal");
        await Expect(card).ToContainTextAsync("Cancel Severn");
        await card.Button("Cancel booking").ClickAsync();
        await card.Button("Yes, cancel it").ClickAsync();
        await Expect(card).ToContainTextAsync("Cancelled");

        await ana.GoToAsync("My bookings");
        await Expect(ana.Row("Severn")).ToContainTextAsync("Cancelled");
    }
}
