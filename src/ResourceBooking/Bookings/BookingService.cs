using System.Globalization;
using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ResourceBooking.Data;
using ResourceBooking.Data.Entities;
using ResourceBooking.Shared;
using ResourceBooking.Shared.Dtos;

namespace ResourceBooking.Bookings;

/// <summary>A refusal the endpoint turns into Problem Details.</summary>
public sealed record ApiError(int Status, string Code, string Message, IReadOnlyList<RuleViolation>? Violations = null)
{
    public static ApiError BadRequest(string message) => new(StatusCodes.Status400BadRequest, "BAD_REQUEST", message);
    public static ApiError NotFound(string message) => new(StatusCodes.Status404NotFound, "NOT_FOUND", message);
}

/// <summary>Either a booking or the reason there isn't one.</summary>
public sealed record BookingResult(Booking? Booking, ApiError? Error)
{
    public static implicit operator BookingResult(Booking booking) => new(booking, null);
    public static implicit operator BookingResult(ApiError error) => new(null, error);
}

/// <summary>
/// Creates, edits and cancels bookings, following the design's "Booking, editing and cancelling" steps.
/// Every path runs in one transaction that locks the resource row first, so a booking, a retirement and
/// a change to a kitchen's units on the same resource run one after another. Overlaps are refused by the
/// database; every other rule by <see cref="BookingRules"/>.
/// </summary>
public sealed class BookingService(AppDb db, TimeProvider clock, TimeZoneInfo zone)
{
    private const string OverlapState = "23P01";

    public Task<BookingResult> CreateAsync(BookingRequest request, Guid callerId, CancellationToken ct) =>
        SaveAsync(existingId: null, request, callerId, ct);

    public Task<BookingResult> UpdateAsync(Guid id, BookingRequest request, Guid callerId, CancellationToken ct) =>
        SaveAsync(id, request, callerId, ct);

    public async Task<BookingResult> CancelAsync(Guid id, Guid callerId, Guid? proposalId, CancellationToken ct)
    {
        var resourceId = await db.Bookings.Where(b => b.Id == id && b.UserId == callerId).Select(b => (Guid?)b.ResourceId).SingleOrDefaultAsync(ct);
        if (resourceId is null)
        {
            return ApiError.NotFound("No such booking");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockResourceAsync(resourceId.Value, ct);
        if (await LockCallerAsync(callerId, ct) is { } inactive)
        {
            return inactive;
        }
        var lockedBooking = await LockOwnBookingAsync(id, callerId, ct);
        if (lockedBooking.Error is not null)
        {
            return lockedBooking;
        }

        var booking = lockedBooking.Booking!;
        booking.Status = BookingStatus.Cancelled;
        booking.CancelReason = "Cancelled by its owner";
        await LinkProposalAsync(proposalId, callerId, ProposalKind.Cancel, booking, request: null, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return booking;
    }

    /// <summary>
    /// Cancels every confirmed booking that matches and hasn't started, one row at a time so the audit
    /// interceptor records each, and tells each active owner other than the actor. Used by retirement and
    /// deactivation, which hold their own locks.
    /// </summary>
    public async Task<int> CancelFutureBookingsAsync(Expression<Func<Booking, bool>> filter, string reason, Guid? actorId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var ids = await db.Bookings.Where(filter)
            .Where(b => b.Status == BookingStatus.Confirmed && (b.DeliveryAt ?? b.StartsAt) > now)
            .Select(b => b.Id)
            .ToArrayAsync(ct);
        // Lock the rows in id order and re-check them, so two cascades over the same bookings (a retirement and
        // a deactivation) cancel each one once, and never deadlock on each other.
        var bookings = await db.Bookings
            .FromSql($"SELECT * FROM bookings WHERE id = ANY({ids}) AND status = 'Confirmed' ORDER BY id FOR NO KEY UPDATE")
            .ToListAsync(ct);
        var resourceIds = bookings.Select(b => b.ResourceId).Distinct().ToList();
        var userIds = bookings.Select(b => b.UserId).Distinct().ToList();
        await db.Resources.Where(r => resourceIds.Contains(r.Id)).LoadAsync(ct);
        await db.Users.Where(u => userIds.Contains(u.Id)).LoadAsync(ct);

        foreach (var booking in bookings)
        {
            booking.Status = BookingStatus.Cancelled;
            booking.CancelReason = reason;
            if (booking.User is { IsActive: true } owner && owner.Id != actorId)
            {
                var when = CompanyTime.ToLocal(booking.DeliveryAt ?? booking.StartsAt, zone);
                db.Notices.Add(new Notice
                {
                    Id = Guid.CreateVersion7(),
                    UserId = owner.Id,
                    BookingId = booking.Id,
                    Message = string.Create(CultureInfo.InvariantCulture,
                        $"Your booking of {booking.Resource!.Name} on {when:d MMM yyyy 'at' HH:mm} was cancelled: {reason}"),
                    CreatedAt = now,
                });
            }
        }
        await db.SaveChangesAsync(ct);
        return bookings.Count;
    }

    /// <summary>
    /// What creating (or, with <paramref name="existingId"/>, changing) this booking would get, without booking:
    /// the same resource, shape, ownership, rule and overlap checks as <see cref="SaveAsync"/>, read without locks.
    /// The assistant proposes only what this accepts; the real booking still runs every check under its locks.
    /// </summary>
    public async Task<ApiError?> PreviewAsync(Guid? existingId, BookingRequest request, Guid callerId, CancellationToken ct)
    {
        var resource = await db.Resources.AsNoTracking().SingleOrDefaultAsync(r => r.Id == request.ResourceId, ct);
        if (resource is null)
        {
            return ApiError.NotFound("No such resource");
        }
        if (await ShapeAsync(request, resource, ct) is { } shapeError)
        {
            return shapeError;
        }
        var candidate = Candidate(request, resource);
        if (candidate is null)
        {
            return ApiError.BadRequest("A time doesn't exist in the company's time zone, or was sent with an offset");
        }
        if (!await db.Users.AnyAsync(u => u.Id == callerId && u.IsActive, ct))
        {
            return new ApiError(StatusCodes.Status401Unauthorized, "INACTIVE", "Your account is no longer active");
        }
        var now = clock.GetUtcNow();
        var bookingId = existingId ?? Guid.Empty;
        if (existingId is { } id)
        {
            var existing = await db.Bookings.AsNoTracking().SingleOrDefaultAsync(b => b.Id == id && b.UserId == callerId, ct);
            if (existing is null)
            {
                return ApiError.NotFound("No such booking");
            }
            if (existing.Status != BookingStatus.Confirmed)
            {
                return new ApiError(StatusCodes.Status409Conflict, "NOT_CONFIRMED", "That booking is already cancelled");
            }
            if ((existing.DeliveryAt ?? existing.StartsAt) <= now)
            {
                return new ApiError(StatusCodes.Status422UnprocessableEntity, "ALREADY_STARTED", "That booking has already started");
            }
            if (existing.ResourceId != resource.Id)
            {
                return ApiError.BadRequest("A booking can't move to another resource; cancel it and book again");
            }
        }

        var context = new RuleContext(
            await db.Bookings.CountAsync(b => b.UserId == callerId && b.ResourceId == resource.Id
                && b.Status == BookingStatus.Confirmed && b.EndsAt > now && b.Id != bookingId, ct),
            resource.Type == ResourceType.Catering ? await FreeUnitAsync(resource, candidate.Start, bookingId, ct) : null);
        var violations = BookingRules.Check(candidate, resource, context, now, zone);
        if (violations.Count > 0)
        {
            return new ApiError(StatusCodes.Status422UnprocessableEntity, "RULES", "The booking breaks a rule", violations);
        }
        if (resource.Type != ResourceType.Catering)
        {
            // The same overlap the database refuses: [start, end + gap) against each booking's [start, blocked_until).
            var blockedUntil = candidate.End + Turnaround(resource);
            var clash = await db.Bookings.AnyAsync(b => b.ResourceId == resource.Id && b.Status == BookingStatus.Confirmed
                && b.Id != bookingId && b.StartsAt < blockedUntil && candidate.Start < b.BlockedUntil, ct);
            if (clash)
            {
                return new ApiError(StatusCodes.Status409Conflict, ApiCodes.Overlap, OverlapMessage(resource));
            }
        }
        return null;
    }

    private async Task<BookingResult> SaveAsync(Guid? existingId, BookingRequest request, Guid callerId, CancellationToken ct)
    {
        var resource = await db.Resources.AsNoTracking().SingleOrDefaultAsync(r => r.Id == request.ResourceId, ct);
        if (resource is null)
        {
            return ApiError.NotFound("No such resource");
        }
        if (await ShapeAsync(request, resource, ct) is { } shapeError)
        {
            return shapeError;
        }
        var candidate = Candidate(request, resource);
        if (candidate is null)
        {
            return ApiError.BadRequest("A time doesn't exist in the company's time zone, or was sent with an offset");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        resource = await LockResourceAsync(resource.Id, ct);
        if (request.DeliveryRoomId is { } room)
        {
            // The insert would take this key lock on the delivery room anyway; taking it now keeps every
            // write's lock order the same: resources, then users, then bookings (formal/AdminLocks.tla).
            // Re-read the room's last day under it: the room may have been retired while this order waited.
            var lastDay = await db.Database
                .SqlQuery<DateOnly?>($"SELECT last_day AS \"Value\" FROM resources WHERE id = {room} FOR KEY SHARE")
                .SingleAsync(ct);
            if (lastDay is { } last && last < DateOnly.FromDateTime(request.DeliveryAt!.Value))
            {
                return ApiError.BadRequest("Catering must be delivered to an active meeting room");
            }
        }
        if (await LockCallerAsync(callerId, ct) is { } inactive)
        {
            return inactive;
        }

        Booking booking;
        if (existingId is { } id)
        {
            var locked = await LockOwnBookingAsync(id, callerId, ct);
            if (locked.Error is not null)
            {
                return locked;
            }
            booking = locked.Booking!;
            if (booking.ResourceId != resource.Id)
            {
                return ApiError.BadRequest("A booking can't move to another resource; cancel it and book again");
            }
        }
        else
        {
            booking = new Booking { Id = Guid.CreateVersion7(), ResourceId = resource.Id, UserId = callerId, Status = BookingStatus.Confirmed };
        }

        var now = clock.GetUtcNow();
        var context = new RuleContext(
            await db.Bookings.CountAsync(b => b.UserId == callerId && b.ResourceId == resource.Id
                && b.Status == BookingStatus.Confirmed && b.EndsAt > now && b.Id != booking.Id, ct),
            resource.Type == ResourceType.Catering ? await FreeUnitAsync(resource, candidate.Start, booking.Id, ct) : null);

        var violations = BookingRules.Check(candidate, resource, context, now, zone);
        if (violations.Count > 0)
        {
            return new ApiError(StatusCodes.Status422UnprocessableEntity, "RULES", "The booking breaks a rule", violations);
        }

        booking.StartsAt = candidate.Start;
        booking.EndsAt = candidate.End;
        booking.BlockedUntil = candidate.End + Turnaround(resource);
        booking.Attendees = request.Attendees;
        booking.Title = Title(request);
        booking.DeliveryAt = candidate.DeliveryAt;
        booking.DeliveryRoomId = resource.Type == ResourceType.Catering ? request.DeliveryRoomId : null;
        booking.UnitNo = context.FreeUnit ?? 0;
        if (existingId is null)
        {
            db.Bookings.Add(booking);
        }
        await LinkProposalAsync(request.ProposalId, callerId, existingId is null ? ProposalKind.Create : ProposalKind.Change, booking, request, ct);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: OverlapState })
        {
            return new ApiError(StatusCodes.Status409Conflict, ApiCodes.Overlap, OverlapMessage(resource));
        }
        await transaction.CommitAsync(ct);
        return booking;
    }

    /// <summary>A request shaped for the wrong kind of resource is a 400, before any rule runs.</summary>
    private async Task<ApiError?> ShapeAsync(BookingRequest request, Resource resource, CancellationToken ct)
    {
        if (resource.Type != ResourceType.Catering)
        {
            return request.DeliveryAt is null && request.DeliveryRoomId is null && request.Start is not null && request.End is not null
                ? null
                : ApiError.BadRequest($"{resource.Name} needs a start and an end, and no delivery");
        }
        if (request.DeliveryAt is null || request.DeliveryRoomId is not { } roomId)
        {
            return ApiError.BadRequest("A catering order needs a delivery time and a room");
        }
        var room = await db.Resources.AsNoTracking().SingleOrDefaultAsync(r => r.Id == roomId, ct);
        var deliveryDay = DateOnly.FromDateTime(request.DeliveryAt.Value);
        return room is { Type: ResourceType.MeetingRoom } && (room.LastDay is null || room.LastDay >= deliveryDay)
            ? null
            : ApiError.BadRequest("Catering must be delivered to an active meeting room");
    }

    /// <summary>The request's times as UTC; a catering order holds its unit for the whole local day.</summary>
    private BookingCandidate? Candidate(BookingRequest request, Resource resource)
    {
        if (resource.Type == ResourceType.Catering)
        {
            if (CompanyTime.ToUtc(request.DeliveryAt!.Value, zone) is not { } delivery)
            {
                return null;
            }
            var day = DateOnly.FromDateTime(request.DeliveryAt.Value);
            return new BookingCandidate(CompanyTime.StartOfDay(day, zone), CompanyTime.StartOfDay(day.AddDays(1), zone), request.Attendees, delivery);
        }
        return CompanyTime.ToUtc(request.Start!.Value, zone) is { } start && CompanyTime.ToUtc(request.End!.Value, zone) is { } end
            ? new BookingCandidate(start, end, request.Attendees)
            : null;
    }

    private async Task<Resource> LockResourceAsync(Guid resourceId, CancellationToken ct) =>
        await db.Resources.FromSql($"SELECT * FROM resources WHERE id = {resourceId} FOR UPDATE").AsNoTracking().SingleAsync(ct);

    /// <summary>Deactivation takes FOR NO KEY UPDATE on the same row, so a booking can't commit for a user being deactivated.</summary>
    private async Task<ApiError?> LockCallerAsync(Guid callerId, CancellationToken ct)
    {
        var active = await db.Users.FromSql($"SELECT * FROM users WHERE id = {callerId} FOR SHARE")
            .Select(u => u.IsActive).SingleOrDefaultAsync(ct);
        return active ? null : new ApiError(StatusCodes.Status401Unauthorized, "SIGNED_OUT", "Sign in again");
    }

    private async Task<BookingResult> LockOwnBookingAsync(Guid id, Guid callerId, CancellationToken ct)
    {
        var booking = await db.Bookings.FromSql($"SELECT * FROM bookings WHERE id = {id} FOR UPDATE").SingleOrDefaultAsync(ct);
        if (booking is null || booking.UserId != callerId)
        {
            return ApiError.NotFound("No such booking");
        }
        if (booking.Status != BookingStatus.Confirmed)
        {
            return new ApiError(StatusCodes.Status409Conflict, "NOT_CONFIRMED", "That booking is already cancelled");
        }
        if ((booking.DeliveryAt ?? booking.StartsAt) <= clock.GetUtcNow())
        {
            return new ApiError(StatusCodes.Status422UnprocessableEntity, "ALREADY_STARTED", "That booking has already started");
        }
        return booking;
    }

    /// <summary>
    /// The lowest kitchen unit with no confirmed order on that local day, or null when all are taken.
    /// An order's range starts at its local day's start, so equality on StartsAt finds that day's orders
    /// (AvailabilityService counts them the same way).
    /// </summary>
    private async Task<int?> FreeUnitAsync(Resource kitchen, DateTimeOffset dayStart, Guid exceptBookingId, CancellationToken ct)
    {
        var taken = await db.Bookings
            .Where(b => b.ResourceId == kitchen.Id && b.Status == BookingStatus.Confirmed && b.StartsAt == dayStart && b.Id != exceptBookingId)
            .Select(b => b.UnitNo)
            .ToListAsync(ct);
        return Enumerable.Range(0, kitchen.Units).Cast<int?>().FirstOrDefault(unit => !taken.Contains(unit!.Value));
    }

    /// <summary>
    /// Links the booking to the assistant proposal it came from, if that proposal is the caller's, still open,
    /// under 24 hours old and of the kind this endpoint confirms. Otherwise it does nothing: a proposal never
    /// makes a booking fail.
    /// </summary>
    private async Task LinkProposalAsync(Guid? proposalId, Guid callerId, ProposalKind kind, Booking booking, BookingRequest? request, CancellationToken ct)
    {
        if (proposalId is not { } id)
        {
            return;
        }
        // Locked, so two bookings sent with the same proposal can't both confirm it. SKIP LOCKED: a second
        // request books unlinked instead of waiting, which could deadlock against the delivery room's key lock.
        var proposal = await db.Proposals.FromSql($"SELECT * FROM proposals WHERE id = {id} FOR UPDATE SKIP LOCKED").SingleOrDefaultAsync(ct);
        var fresh = proposal is not null && clock.GetUtcNow() - proposal.CreatedAt < TimeSpan.FromHours(24);
        var rightTarget = kind == ProposalKind.Create || proposal?.BookingId == booking.Id;
        if (proposal is null || proposal.UserId != callerId || proposal.Status != ProposalStatus.Open || !fresh
            || proposal.Kind != kind || !rightTarget)
        {
            return;
        }

        proposal.Status = ProposalStatus.Confirmed;
        if (kind != ProposalKind.Cancel)
        {
            booking.ProposalId = proposal.Id;
        }
        if (request is not null)
        {
            proposal.ChangedBeforeConfirm = Proposed(proposal.Payload) is not { } proposed || !Equals(Submitted(proposed), Submitted(request));
        }
    }

    private static BookingRequest? Proposed(string payload)
    {
        try
        {
            return JsonSerializer.Deserialize<BookingRequest>(payload, JsonSerializerOptions.Web);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The values a booking keeps from a request: a catering order's start and end come from its delivery day.</summary>
    private static object Submitted(BookingRequest r) => r.DeliveryAt is null
        ? (r.ResourceId, r.Start, r.End, r.Attendees, Title(r), (DateTime?)null, (Guid?)null)
        : (r.ResourceId, (DateTime?)null, (DateTime?)null, r.Attendees, Title(r), r.DeliveryAt, r.DeliveryRoomId);

    private static string? Title(BookingRequest r) => string.IsNullOrWhiteSpace(r.Title) ? null : r.Title.Trim();

    /// <summary>The gap a resource needs between bookings; only types whose rule fields include it have one.</summary>
    public static TimeSpan Turnaround(Resource resource) =>
        RuleFields.For(resource.Type).Contains(RuleField.Turnaround) ? resource.Turnaround ?? TimeSpan.Zero : TimeSpan.Zero;

    private static string OverlapMessage(Resource resource) => Turnaround(resource) is { } gap && gap > TimeSpan.Zero
        ? $"{resource.Name} is booked then, or within its {(int)gap.TotalMinutes}-minute gap between bookings"
        : $"{resource.Name} is already booked then";
}
