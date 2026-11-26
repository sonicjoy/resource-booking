using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using ResourceBooking.Bookings;
using ResourceBooking.Data;
using ResourceBooking.Data.Entities;
using ResourceBooking.Endpoints;
using ResourceBooking.Shared;
using ResourceBooking.Shared.Dtos;

namespace ResourceBooking.Assistant;

/// <summary>
/// The model's tools for one turn. They run as the signed-in user, whatever a model argument says, and they
/// never book: a propose tool checks the request exactly as booking would (<see cref="BookingService.PreviewAsync"/>)
/// and stores an open proposal only when it would succeed. Refusals come back to the model as text.
/// </summary>
public sealed class AssistantTools(AppDb db, BookingService bookings, AvailabilityService availability,
    TimeProvider clock, TimeZoneInfo zone, Guid userId, Guid conversationId, int turn)
{
    public IList<AITool> Create(Trace trace) =>
    [
        new TracedFunction(AIFunctionFactory.Create(FindFreeResourcesAsync, "find_free_resources"), trace),
        new TracedFunction(AIFunctionFactory.Create(GetMyBookingsAsync, "get_my_bookings"), trace),
        new TracedFunction(AIFunctionFactory.Create(ProposeBookingAsync, "propose_booking"), trace),
        new TracedFunction(AIFunctionFactory.Create(ProposeChangeAsync, "propose_change"), trace),
        new TracedFunction(AIFunctionFactory.Create(ProposeCancellationAsync, "propose_cancellation"), trace),
    ];

    [Description("Lists resources of a type that are free for a window and seat at least the attendees. Times are London wall-clock times like 2026-11-26T09:00. For Catering, start is the delivery time and end can be left out: it lists kitchens with an order free that day, with the ids propose_booking needs.")]
    public async Task<object> FindFreeResourcesAsync(
        [Description("MeetingRoom, Car or Catering")] string type,
        string start, string? end = null, int attendees = 1, CancellationToken ct = default)
    {
        if (!Enum.TryParse<ResourceType>(type, ignoreCase: false, out var kind) || !Enum.IsDefined(kind))
        {
            return "Unknown type: use MeetingRoom, Car or Catering.";
        }
        if (kind == ResourceType.Catering)
        {
            // A kitchen order is delivered at a time and takes a unit for its day; there is no end to check.
            if (Local(start) is not { } delivery || CompanyTime.ToUtc(delivery, zone) is not { } at)
            {
                return "Give the delivery time as a London time like 2026-11-26T12:30.";
            }
            var kitchens = await availability.FreeAsync(kind, at, at, Math.Max(1, attendees), ct);
            return kitchens.Select(r => new { r.Id, r.Name, r.Capacity }).ToList();
        }
        if (Local(start) is not { } from || Local(end) is not { } until || CompanyTime.ToUtc(from, zone) is not { } f
            || CompanyTime.ToUtc(until, zone) is not { } u || u <= f)
        {
            return "Give a start before an end, as London times like 2026-11-26T09:00.";
        }
        var free = await availability.FreeAsync(kind, f, u, Math.Max(1, attendees), ct);
        return free.Select(r => new { r.Id, r.Name, r.Capacity }).ToList();
    }

    [Description("Lists the signed-in user's upcoming confirmed bookings.")]
    public async Task<object> GetMyBookingsAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var mine = await db.Bookings.AsNoTracking().Include(b => b.Resource)
            .Where(b => b.UserId == userId && b.Status == BookingStatus.Confirmed && b.EndsAt > now)
            .OrderBy(b => b.StartsAt).Take(50).ToListAsync(ct);
        return mine.Select(b => new
        {
            bookingId = b.Id,
            resource = b.Resource!.Name,
            start = Text(CompanyTime.ToLocal(b.DeliveryAt ?? b.StartsAt, zone)),
            end = b.DeliveryAt is null ? Text(CompanyTime.ToLocal(b.EndsAt, zone)) : null,
            b.Attendees,
            b.Title,
        }).ToList();
    }

    [Description("Proposes a new booking for the user to confirm; it books nothing. A room or car needs start and end; a catering order needs deliveryAt and a deliveryRoomId instead, and attendees is its portions.")]
    public async Task<string> ProposeBookingAsync(
        [Description("The room, car or kitchen to book. For a catering order this is the kitchen (find it with find_free_resources type Catering), not the room.")] Guid resourceId,
        int attendees, string? start = null, string? end = null,
        string? title = null, string? deliveryAt = null,
        [Description("For a catering order: the meeting room it is delivered to.")] Guid? deliveryRoomId = null,
        CancellationToken ct = default)
    {
        (start, end, title, deliveryAt) = (Blank(start), Blank(end), Blank(title), Blank(deliveryAt));
        if (Unreadable(start, end, deliveryAt) is { } refusal)
        {
            return refusal;
        }
        if (Local(deliveryAt) is { } delivery && await NotAKitchenAsync(resourceId, DateOnly.FromDateTime(delivery), ct) is { } notAKitchen)
        {
            return notAKitchen;
        }
        var request = new BookingRequest
        {
            ResourceId = resourceId,
            Start = Local(start),
            End = Local(end),
            Attendees = attendees,
            Title = title,
            DeliveryAt = Local(deliveryAt),
            DeliveryRoomId = deliveryRoomId,
        };
        return await ProposeAsync(ProposalKind.Create, null, request, ct);
    }

    [Description("Proposes a change to one of the user's own bookings; fields left out keep their current values. It changes nothing until the user confirms.")]
    public async Task<string> ProposeChangeAsync(Guid bookingId, string? start = null, string? end = null, int? attendees = null,
        string? title = null, string? deliveryAt = null, Guid? deliveryRoomId = null, CancellationToken ct = default)
    {
        (start, end, title, deliveryAt) = (Blank(start), Blank(end), Blank(title), Blank(deliveryAt));
        if (Unreadable(start, end, deliveryAt) is { } refusal)
        {
            return refusal;
        }
        var booking = await db.Bookings.AsNoTracking().SingleOrDefaultAsync(b => b.Id == bookingId && b.UserId == userId, ct);
        if (booking is null)
        {
            return "Not proposed: no such booking of yours.";
        }
        var catering = booking.DeliveryAt is not null;
        var request = new BookingRequest
        {
            ResourceId = booking.ResourceId,
            Start = catering ? null : Local(start) ?? CompanyTime.ToLocal(booking.StartsAt, zone),
            End = catering ? null : Local(end) ?? CompanyTime.ToLocal(booking.EndsAt, zone),
            Attendees = attendees ?? booking.Attendees,
            Title = title ?? booking.Title,
            DeliveryAt = catering ? Local(deliveryAt) ?? CompanyTime.ToLocal(booking.DeliveryAt!.Value, zone) : null,
            DeliveryRoomId = catering ? deliveryRoomId ?? booking.DeliveryRoomId : null,
        };
        return await ProposeAsync(ProposalKind.Change, bookingId, request, ct);
    }

    [Description("Proposes cancelling one of the user's own bookings. It cancels nothing until the user confirms.")]
    public async Task<string> ProposeCancellationAsync(Guid bookingId, CancellationToken ct = default)
    {
        var booking = await db.Bookings.AsNoTracking().Include(b => b.Resource).SingleOrDefaultAsync(b => b.Id == bookingId && b.UserId == userId, ct);
        if (booking is null || booking.Status != BookingStatus.Confirmed || (booking.DeliveryAt ?? booking.StartsAt) <= clock.GetUtcNow())
        {
            return "Not proposed: no upcoming booking of yours with that id.";
        }
        var summary = CancelSummary(booking);
        await StoreAsync(ProposalKind.Cancel, bookingId, "{}");
        return $"Proposed: {summary}. The user confirms it.";
    }

    private async Task<string> ProposeAsync(ProposalKind kind, Guid? bookingId, BookingRequest request, CancellationToken ct)
    {
        var invalid = ApiResults.Validate(request);
        if (invalid.Count > 0)
        {
            return "Not proposed: " + string.Join(" ", invalid.Select(r => $"{string.Join(",", r.MemberNames)}: {r.ErrorMessage}"));
        }
        if (await bookings.PreviewAsync(bookingId, request, userId, ct) is { } error)
        {
            var reasons = error.Violations is { Count: > 0 } v ? string.Join(" ", v.Select(x => x.Message)) : error.Message;
            return $"Not proposed: {reasons}";
        }
        var id = await StoreAsync(kind, bookingId, JsonSerializer.Serialize(request, JsonSerializerOptions.Web));
        return $"Proposed ({id}): {await SummaryAsync(request, ct)}. The user confirms it in the form.";
    }

    /// <summary>
    /// Saves an open proposal and returns its id, or returns the id of an identical proposal already open this turn:
    /// the model sometimes proposes the same thing twice. Saved without the request's token: a proposal half-saved
    /// when the turn timed out would stay tracked and be sent again by the reply's trace save. A failed save is let
    /// go of for the same reason.
    /// </summary>
    private async Task<Guid> StoreAsync(ProposalKind kind, Guid? bookingId, string payload)
    {
        // Compared as JSON: jsonb stores its own normalised text.
        var wanted = JsonNode.Parse(payload);
        var same = await db.Proposals.AsNoTracking()
            .Where(p => p.ConversationId == conversationId && p.Turn == turn && p.Status == ProposalStatus.Open
                && p.Kind == kind && p.BookingId == bookingId)
            .Select(p => new { p.Id, p.Payload }).ToListAsync(CancellationToken.None);
        if (same.FirstOrDefault(p => JsonNode.DeepEquals(JsonNode.Parse(p.Payload), wanted)) is { } existing)
        {
            return existing.Id;
        }
        var proposal = new Proposal
        {
            Id = Guid.CreateVersion7(),
            ConversationId = conversationId,
            UserId = userId,
            Turn = turn,
            Kind = kind,
            BookingId = bookingId,
            Payload = payload,
            Status = ProposalStatus.Open,
            CreatedAt = clock.GetUtcNow(),
        };
        db.Proposals.Add(proposal);
        try
        {
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch
        {
            db.Entry(proposal).State = EntityState.Detached;
            throw;
        }
        return proposal.Id;
    }

    /// <summary>A proposal in words, for the model and the panel's card.</summary>
    public async Task<string> SummaryAsync(BookingRequest r, CancellationToken ct)
    {
        var names = await db.Resources.AsNoTracking().Where(x => x.Id == r.ResourceId || x.Id == r.DeliveryRoomId)
            .ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var name = names.GetValueOrDefault(r.ResourceId!.Value, "?");
        return r.DeliveryAt is { } delivery
            ? $"{name}: {r.Attendees} portions to {names.GetValueOrDefault(r.DeliveryRoomId ?? Guid.Empty, "?")}, {Text(delivery)}"
            : $"{name}, {Text(r.Start!.Value)} to {r.End!.Value:HH:mm}, {r.Attendees} people";
    }

    /// <summary>A cancellation in words: the booking's resource and when it starts or is delivered.</summary>
    public async Task<string> CancelSummaryAsync(Guid bookingId, CancellationToken ct) =>
        await db.Bookings.AsNoTracking().Include(b => b.Resource).SingleOrDefaultAsync(b => b.Id == bookingId, ct) is { } booking
            ? CancelSummary(booking)
            : "Cancel a booking";

    private string CancelSummary(Booking booking) =>
        $"Cancel {booking.Resource!.Name}, {Text(CompanyTime.ToLocal(booking.DeliveryAt ?? booking.StartsAt, zone))}";

    /// <summary>A blank argument from the model means "not given", the same as leaving it out.</summary>
    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;

    private static readonly string[] Formats = ["yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd HH:mm"];

    /// <summary>A London wall-clock date and time as the model writes it; anything else (a time with no date, an offset) is null.</summary>
    private static DateTime? Local(string? text) =>
        DateTime.TryParseExact(text?.Trim(), Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;

    private const int KitchensListed = 10;

    /// <summary>
    /// A catering order sent with a room (or anything but a kitchen) as its resource: the refusal says which field is
    /// which and lists up to <see cref="KitchensListed"/> kitchens taking orders that day, so the model can send it
    /// again without another search.
    /// </summary>
    private async Task<string?> NotAKitchenAsync(Guid resourceId, DateOnly day, CancellationToken ct)
    {
        if (await db.Resources.AsNoTracking().AnyAsync(r => r.Id == resourceId && r.Type == ResourceType.Catering, ct))
        {
            return null;
        }
        var kitchens = await db.Resources.AsNoTracking()
            .Where(r => r.Type == ResourceType.Catering && (r.LastDay == null || r.LastDay >= day))
            .OrderBy(r => r.Name).Select(r => new { r.Name, r.Id }).ToListAsync(ct);
        var listed = string.Join(", ", kitchens.Take(KitchensListed).Select(k => $"{k.Name} ({k.Id})"));
        var more = kitchens.Count > KitchensListed ? $" and {kitchens.Count - KitchensListed} more (use find_free_resources)" : "";
        return $"Not proposed: for a catering order resourceId is the kitchen and deliveryRoomId is the room. Kitchens: {(listed == "" ? "none that day" : listed)}{more}.";
    }

    /// <summary>A refusal naming the first time given that isn't a full date and time, so it isn't silently ignored.</summary>
    private static string? Unreadable(params string?[] times) =>
        times.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t) && Local(t) is null) is { } bad
            ? $"Not proposed: \"{bad}\" isn't a London date and time; write it like 2026-11-26T09:00."
            : null;

    private static string Text(DateTime t) => t.ToString("ddd d MMM yyyy HH:mm", CultureInfo.InvariantCulture);
}
