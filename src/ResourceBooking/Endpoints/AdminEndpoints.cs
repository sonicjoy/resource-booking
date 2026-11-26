using ResourceBooking.Auth;
using ResourceBooking.Data;

namespace ResourceBooking.Endpoints;

public static class AdminEndpoints
{
    public static RouteGroupBuilder MapAdminEndpoints(this RouteGroupBuilder api)
    {
        var admin = api.MapGroup("/admin").RequireAuthorization(CookieSetup.AdminPolicy);

        // Resets the public demo to its seed data. Anyone signed in as an admin can do this,
        // which may interrupt another visitor: an accepted cost of the demo.
        admin.MapPost("/reseed", async (AppDb db, TimeProvider clock, TimeZoneInfo zone, HttpContext http, ILoggerFactory logs, CancellationToken ct) =>
        {
            logs.CreateLogger("Reseed").LogInformation("Demo data reseeded by user {UserId}", http.User.UserId());
            await Seed.ReseedAsync(db, clock, zone, ct);
            return Results.NoContent();
        }).RequireRateLimiting(ApiGuards.ReseedLimit);

        return api;
    }
}
