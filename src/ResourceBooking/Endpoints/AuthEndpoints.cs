using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using ResourceBooking.Auth;
using ResourceBooking.Data;
using ResourceBooking.Shared.Dtos;

namespace ResourceBooking.Endpoints;

/// <summary>Mock SSO: pick an active user and get a session cookie.</summary>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth");

        auth.MapGet("/sign-in-options", async (AppDb db, CancellationToken ct) =>
            await db.Users.AsNoTracking()
                .Where(u => u.IsActive)
                .OrderBy(u => u.DisplayName)
                .Select(u => new SignInOption(u.Id, u.DisplayName, u.Email, u.Role))
                .ToListAsync(ct));

        auth.MapPost("/sign-in", async (SignInRequest request, AppDb db, HttpContext http, CancellationToken ct) =>
        {
            var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == request.UserId && u.IsActive, ct);
            if (user is null)
            {
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No active user with that id");
            }
            await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, CookieSetup.PrincipalFor(user));
            return Results.Ok(new CurrentUser(user.Id, user.DisplayName, user.Email, user.Role));
        });

        auth.MapPost("/sign-out", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        });

        auth.MapGet("/me", async (HttpContext http, AppDb db, CancellationToken ct) =>
        {
            var userId = http.User.UserId();
            var me = await db.Users.AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => new CurrentUser(u.Id, u.DisplayName, u.Email, u.Role))
                .SingleOrDefaultAsync(ct);
            return me is null ? Results.Unauthorized() : Results.Ok(me);
        }).RequireAuthorization();

        return app;
    }
}
