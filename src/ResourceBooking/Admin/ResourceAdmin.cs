using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ResourceBooking.Bookings;
using ResourceBooking.Data;
using ResourceBooking.Data.Entities;
using ResourceBooking.Shared;
using ResourceBooking.Shared.Dtos;

namespace ResourceBooking.Admin;

/// <summary>
/// Adds, edits and retires resources. Edits and retirement lock the resource row first, as every booking
/// write does, so they run one after another with bookings of that resource.
/// </summary>
public sealed class ResourceAdmin(AppDb db, BookingService bookings, TimeProvider clock, TimeZoneInfo zone)
{
    private const string UniqueViolation = "23505";

    public async Task<AdminResult<Resource>> AddAsync(ResourceRequest request, CancellationToken ct)
    {
        var resource = new Resource { Id = Guid.CreateVersion7(), Name = "", Type = request.Type };
        Apply(request, resource);
        db.Resources.Add(resource);
        return await SaveAsync(resource, ct);
    }

    public async Task<AdminResult<Resource>> UpdateAsync(Guid id, ResourceRequest request, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (await LockAsync(id, ct) is not { } resource)
        {
            return ApiError.NotFound("No such resource");
        }
        if (request.Type != resource.Type)
        {
            return ApiError.BadRequest("A resource's type can't change");
        }
        if (resource.Type == ResourceType.Catering && request.Units < resource.Units)
        {
            var now = clock.GetUtcNow();
            var held = await db.Bookings.AnyAsync(b => b.ResourceId == id && b.Status == BookingStatus.Confirmed
                && b.EndsAt > now && b.UnitNo >= request.Units, ct);
            if (held)
            {
                return new ApiError(StatusCodes.Status422UnprocessableEntity, "UNITS_IN_USE",
                    $"{resource.Name} has future orders on unit {request.Units + 1} or above; cancel them first");
            }
        }
        Apply(request, resource);
        var result = await SaveAsync(resource, ct);
        if (result.Error is null)
        {
            await transaction.CommitAsync(ct);
        }
        return result;
    }

    /// <summary>Sets the last bookable day and cancels the bookings after it, telling their owners.</summary>
    public async Task<AdminResult<CascadeResult>> RetireAsync(Guid id, DateOnly lastDay, Guid actorId, CancellationToken ct)
    {
        if (!CompanyTime.InRange(lastDay))
        {
            return ApiError.BadRequest("Give a last day before the year 9999");
        }
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (await LockAsync(id, ct) is not { } resource)
        {
            return ApiError.NotFound("No such resource");
        }
        if (lastDay < CompanyTime.LocalDate(clock.GetUtcNow(), zone))
        {
            return new ApiError(StatusCodes.Status422UnprocessableEntity, "LAST_DAY_PAST", "The last day can't be before today");
        }
        var after = CompanyTime.StartOfDay(lastDay.AddDays(1), zone);
        var across = await db.Bookings.AnyAsync(b => b.ResourceId == id && b.Status == BookingStatus.Confirmed
            && b.StartsAt < after && b.EndsAt > after, ct);
        if (across)
        {
            return new ApiError(StatusCodes.Status422UnprocessableEntity, "IN_USE",
                $"{resource.Name} has a booking running past {lastDay.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}; choose a later last day or cancel it first");
        }
        resource.LastDay = lastDay;
        await db.SaveChangesAsync(ct);
        var reason = $"{resource.Name} can't be booked after {lastDay.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}";
        var cancelled = await bookings.CancelFutureBookingsAsync(b => b.ResourceId == id && b.StartsAt >= after, reason, actorId, ct);
        await transaction.CommitAsync(ct);
        return new CascadeResult(cancelled);
    }

    private async Task<Resource?> LockAsync(Guid id, CancellationToken ct) =>
        await db.Resources.FromSql($"SELECT * FROM resources WHERE id = {id} FOR UPDATE").SingleOrDefaultAsync(ct);

    /// <summary>Copies the request onto the resource, keeping only the rule fields its type uses.</summary>
    private static void Apply(ResourceRequest request, Resource resource)
    {
        var uses = RuleFields.For(resource.Type);
        T? Use<T>(RuleField field, T? value) where T : struct => uses.Contains(field) ? value : null;

        resource.Name = request.Name.Trim();
        resource.Capacity = request.Capacity;
        resource.Units = uses.Contains(RuleField.Units) ? request.Units : 1;
        resource.OpensAt = Use(RuleField.OpeningHours, request.OpensAt);
        resource.ClosesAt = Use(RuleField.OpeningHours, request.ClosesAt);
        resource.MinDuration = Use(RuleField.MinDuration, request.MinDuration);
        resource.MaxDuration = Use(RuleField.MaxDuration, request.MaxDuration);
        resource.MinNotice = Use(RuleField.MinNotice, request.MinNotice);
        resource.MaxDaysAhead = Use(RuleField.MaxDaysAhead, request.MaxDaysAhead);
        resource.Turnaround = Use(RuleField.Turnaround, request.Turnaround);
        resource.MaxFuturePerUser = Use(RuleField.MaxFuturePerUser, request.MaxFuturePerUser);
    }

    private async Task<AdminResult<Resource>> SaveAsync(Resource resource, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return resource;
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: UniqueViolation })
        {
            return new ApiError(StatusCodes.Status409Conflict, "DUPLICATE", $"{resource.Name} is already a resource's name");
        }
    }
}
