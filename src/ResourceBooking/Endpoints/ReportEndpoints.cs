using ResourceBooking.Auth;
using ResourceBooking.Bookings;
using ResourceBooking.Reports;

namespace ResourceBooking.Endpoints;

/// <summary>Admin: utilisation and history.</summary>
public static class ReportEndpoints
{
    public static RouteGroupBuilder MapReportEndpoints(this RouteGroupBuilder api)
    {
        var admin = api.MapGroup("").RequireAuthorization(CookieSetup.AdminPolicy);

        admin.MapGet("/reports/utilisation", async (DateOnly from, DateOnly to, Utilisation utilisation, CancellationToken ct) =>
        {
            if (to < from || to.DayNumber - from.DayNumber >= Utilisation.MaxDays || !CompanyTime.InRange(to))
            {
                return ApiResults.Problem(ApiError.BadRequest($"Give a from date on or before the to date, at most {Utilisation.MaxDays} days apart"));
            }
            return Results.Ok(await utilisation.ForAsync(from, to, ct));
        });

        admin.MapGet("/history", async (string? entityType, Guid? entityId, DateOnly? from, DateOnly? to, History history, CancellationToken ct) =>
        {
            if (entityType is not null && !History.EntityTypes.Contains(entityType))
            {
                return ApiResults.Problem(ApiError.BadRequest("entityType is User, Resource or Booking"));
            }
            if ((from is { } f && !CompanyTime.InRange(f)) || (to is { } t && !CompanyTime.InRange(t)))
            {
                return ApiResults.Problem(ApiError.BadRequest("Give dates before the year 9999"));
            }
            return Results.Ok(await history.ListAsync(entityType, entityId, from, to, ct));
        });

        return api;
    }
}
