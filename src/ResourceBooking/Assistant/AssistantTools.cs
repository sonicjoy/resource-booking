using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
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

    [Description("Lists resources of a type that are free for a window and seat at least the attendees. Times are London wall-clock times like 2026-11-26T09:00.")]
    public async Task<object> FindFreeResourcesAsync(
        [Description("MeetingRoom, Car or Catering")] string type,
        string start, string end, int attendees, CancellationToken ct)
    {
        if (!Enum.TryParse<ResourceType>(type, ignoreCase: false, out var kind) || !Enum.IsDefined(kind))
        {
            return "Unknown type: use MeetingRoom, Car or Catering.";
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
    public async Task<string> ProposeBookingAsync(Guid resourceId, int attendees, string? start = null, string? end = null,
        string? title = null, string? deliveryAt = null, Guid? deliveryRoomId = null, CancellationToken ct = default)
    {
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
        await StoreAsync(ProposalKind.Cancel, bookingId, "{}", ct);
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
        var id = await StoreAsync(kind, bookingId, JsonSerializer.Serialize(request, JsonSerializerOptions.Web), ct);
        return $"Proposed ({id}): {await SummaryAsync(request, ct)}. The user confirms it in the form.";
    }

    private async Task<Guid> StoreAsync(ProposalKind kind, Guid? bookingId, string payload, CancellationToken ct)
    {
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
        await db.SaveChangesAsync(ct);
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

    private static DateTime? Local(string? text) =>
        DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;

    private static string Text(DateTime t) => t.ToString("ddd d MMM yyyy HH:mm", CultureInfo.InvariantCulture);
}
