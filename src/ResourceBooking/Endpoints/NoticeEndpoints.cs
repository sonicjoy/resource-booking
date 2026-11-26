using Microsoft.EntityFrameworkCore;
using ResourceBooking.Auth;
using ResourceBooking.Bookings;
using ResourceBooking.Data;
using ResourceBooking.Shared.Dtos;

namespace ResourceBooking.Endpoints;

/// <summary>The in-app outbox: notices about the caller's cancelled bookings.</summary>
public static class NoticeEndpoints
{
    public static RouteGroupBuilder MapNoticeEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/notices", async (HttpContext http, AppDb db, TimeZoneInfo zone, CancellationToken ct) =>
        {
            var userId = http.User.UserId();
            var notices = await db.Notices.AsNoTracking().Where(n => n.UserId == userId).OrderByDescending(n => n.CreatedAt).ToListAsync(ct);
            return notices.Select(n => new NoticeDto(n.Id, n.BookingId, n.Message, CompanyTime.ToLocal(n.CreatedAt, zone), n.ReadAt is not null)).ToList();
        });

        api.MapPost("/notices/{id:guid}/read", async (Guid id, HttpContext http, AppDb db, TimeProvider clock, CancellationToken ct) =>
        {
            var userId = http.User.UserId();
            var notice = await db.Notices.SingleOrDefaultAsync(n => n.Id == id && n.UserId == userId, ct);
            if (notice is null)
            {
                return ApiResults.Problem(ApiError.NotFound("No such notice"));
            }
            notice.ReadAt ??= clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        return api;
    }
}
