using Microsoft.EntityFrameworkCore;
using ResourceBooking.Auth;
using ResourceBooking.Bookings;
using ResourceBooking.Data;
using ResourceBooking.Data.Entities;
using ResourceBooking.Shared.Dtos;

namespace ResourceBooking.Endpoints;

/// <summary>The caller's own bookings: list, read, create, edit and cancel. Someone else's booking is a 404.</summary>
public static class BookingEndpoints
{
    public static RouteGroupBuilder MapBookingEndpoints(this RouteGroupBuilder api)
    {
        var bookings = api.MapGroup("/bookings");

        bookings.MapGet("/mine", async (DateOnly? from, DateOnly? to, HttpContext http, AppDb db, TimeZoneInfo zone, CancellationToken ct) =>
        {
            if ((from is { } f && !CompanyTime.InRange(f)) || (to is { } t && !CompanyTime.InRange(t)))
            {
                return ApiResults.Problem(ApiError.BadRequest("Give dates before the year 9999"));
            }
            var userId = http.User.UserId();
            var query = db.Bookings.AsNoTracking().Include(b => b.Resource).Where(b => b.UserId == userId);
            if (from is { } first)
            {
                var start = CompanyTime.StartOfDay(first, zone);
                query = query.Where(b => b.EndsAt > start);
            }
            if (to is { } last)
            {
                var end = CompanyTime.StartOfDay(last.AddDays(1), zone);
                query = query.Where(b => b.StartsAt < end);
            }
            var list = await query.OrderBy(b => b.StartsAt).ToListAsync(ct);
            return Results.Ok(list.Select(b => ToDto(b, zone)).ToList());
        });

        bookings.MapGet("/{id:guid}", async (Guid id, HttpContext http, AppDb db, TimeZoneInfo zone, CancellationToken ct) =>
        {
            var userId = http.User.UserId();
            var booking = await db.Bookings.AsNoTracking().Include(b => b.Resource).SingleOrDefaultAsync(b => b.Id == id && b.UserId == userId, ct);
            return booking is null ? ApiResults.Problem(ApiError.NotFound("No such booking")) : Results.Ok(ToDto(booking, zone));
        });

        bookings.MapPost("/", async (BookingRequest request, HttpContext http, BookingService service, AppDb db, TimeZoneInfo zone, CancellationToken ct) =>
        {
            if (ApiResults.Invalid(request) is { } invalid)
            {
                return invalid;
            }
            var result = await service.CreateAsync(request, http.User.UserId()!.Value, ct);
            return result.Error is { } error
                ? ApiResults.Problem(error)
                : Results.Created($"/api/bookings/{result.Booking!.Id}", await LoadDtoAsync(db, result.Booking.Id, zone, ct));
        });

        bookings.MapPut("/{id:guid}", async (Guid id, BookingRequest request, HttpContext http, BookingService service, AppDb db, TimeZoneInfo zone, CancellationToken ct) =>
        {
            if (ApiResults.Invalid(request) is { } invalid)
            {
                return invalid;
            }
            var result = await service.UpdateAsync(id, request, http.User.UserId()!.Value, ct);
            return result.Error is { } error ? ApiResults.Problem(error) : Results.Ok(await LoadDtoAsync(db, id, zone, ct));
        });

        bookings.MapDelete("/{id:guid}", async (Guid id, Guid? proposalId, HttpContext http, BookingService service, CancellationToken ct) =>
        {
            var result = await service.CancelAsync(id, http.User.UserId()!.Value, proposalId, ct);
            return result.Error is { } error ? ApiResults.Problem(error) : Results.NoContent();
        });

        return api;
    }

    private static async Task<BookingDto> LoadDtoAsync(AppDb db, Guid id, TimeZoneInfo zone, CancellationToken ct) =>
        ToDto(await db.Bookings.AsNoTracking().Include(b => b.Resource).SingleAsync(b => b.Id == id, ct), zone);

    public static BookingDto ToDto(Booking b, TimeZoneInfo zone) => new(
        b.Id,
        b.ResourceId,
        b.Resource!.Name,
        b.Resource.Type,
        CompanyTime.ToLocal(b.StartsAt, zone),
        CompanyTime.ToLocal(b.EndsAt, zone),
        b.Attendees,
        b.Title,
        b.DeliveryAt is { } delivery ? CompanyTime.ToLocal(delivery, zone) : null,
        b.DeliveryRoomId,
        b.Status);
}
