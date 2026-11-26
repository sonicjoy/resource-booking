using Microsoft.EntityFrameworkCore;
using ResourceBooking.Admin;
using ResourceBooking.Auth;
using ResourceBooking.Data;
using ResourceBooking.Data.Entities;
using ResourceBooking.Shared.Dtos;

namespace ResourceBooking.Endpoints;

/// <summary>Admin: users. Deactivation is its own action because it cancels the user's future bookings.</summary>
public static class UserEndpoints
{
    public static RouteGroupBuilder MapUserEndpoints(this RouteGroupBuilder api)
    {
        var users = api.MapGroup("/users").RequireAuthorization(CookieSetup.AdminPolicy);

        users.MapGet("/", async (AppDb db, CancellationToken ct) =>
            (await db.Users.AsNoTracking().OrderBy(u => u.DisplayName).ToListAsync(ct)).Select(ToDto).ToList());

        users.MapGet("/{id:guid}", async (Guid id, AppDb db, CancellationToken ct) =>
            await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id, ct) is { } user
                ? Results.Ok(ToDto(user))
                : ApiResults.Problem(Bookings.ApiError.NotFound("No such user")));

        users.MapPost("/", async (UserRequest request, UserAdmin admin, CancellationToken ct) =>
        {
            if (ApiResults.Invalid(request) is { } invalid)
            {
                return invalid;
            }
            var result = await admin.AddAsync(request, ct);
            return result.Error is { } error ? ApiResults.Problem(error) : Results.Created($"/api/users/{result.Value!.Id}", ToDto(result.Value));
        });

        users.MapPut("/{id:guid}", async (Guid id, UserRequest request, UserAdmin admin, CancellationToken ct) =>
        {
            if (ApiResults.Invalid(request) is { } invalid)
            {
                return invalid;
            }
            var result = await admin.UpdateAsync(id, request, ct);
            return result.Error is { } error ? ApiResults.Problem(error) : Results.Ok(ToDto(result.Value!));
        });

        users.MapPost("/{id:guid}/deactivation", async (Guid id, HttpContext http, UserAdmin admin, CancellationToken ct) =>
        {
            var result = await admin.DeactivateAsync(id, http.User.UserId()!.Value, ct);
            return result.Error is { } error ? ApiResults.Problem(error) : Results.Ok(result.Value);
        });

        return api;
    }

    public static UserDto ToDto(User u) => new(u.Id, u.Email, u.DisplayName, u.Role, u.IsActive);
}
