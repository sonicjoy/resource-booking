using ResourceBooking.Auth;
using ResourceBooking.Data;

namespace ResourceBooking.Endpoints;

public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin").RequireAuthorization(CookieSetup.AdminPolicy);

        // Resets the public demo to its seed data. Anyone signed in as an admin can do this,
        // which may interrupt another visitor: an accepted cost of the demo.
        admin.MapPost("/reseed", async (AppDb db, TimeProvider clock, TimeZoneInfo zone, CancellationToken ct) =>
        {
            await Seed.ReseedAsync(db, clock, zone, ct);
            return Results.NoContent();
        });

        return app;
    }
}
