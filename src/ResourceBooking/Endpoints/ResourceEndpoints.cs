using ResourceBooking.Admin;
using ResourceBooking.Auth;
using ResourceBooking.Shared.Dtos;

namespace ResourceBooking.Endpoints;

/// <summary>Admin: add, edit and retire resources. Reading them is in <see cref="AvailabilityEndpoints"/>.</summary>
public static class ResourceEndpoints
{
    public static RouteGroupBuilder MapResourceEndpoints(this RouteGroupBuilder api)
    {
        var resources = api.MapGroup("/resources").RequireAuthorization(CookieSetup.AdminPolicy);

        resources.MapPost("/", async (ResourceRequest request, ResourceAdmin admin, CancellationToken ct) =>
        {
            if (ApiResults.Invalid(request) is { } invalid)
            {
                return invalid;
            }
            var result = await admin.AddAsync(request, ct);
            return result.Error is { } error
                ? ApiResults.Problem(error)
                : Results.Created($"/api/resources/{result.Value!.Id}", AvailabilityEndpoints.ToDto(result.Value));
        });

        resources.MapPut("/{id:guid}", async (Guid id, ResourceRequest request, ResourceAdmin admin, CancellationToken ct) =>
        {
            if (ApiResults.Invalid(request) is { } invalid)
            {
                return invalid;
            }
            var result = await admin.UpdateAsync(id, request, ct);
            return result.Error is { } error ? ApiResults.Problem(error) : Results.Ok(AvailabilityEndpoints.ToDto(result.Value!));
        });

        resources.MapPost("/{id:guid}/retirement", async (Guid id, RetirementRequest request, HttpContext http, ResourceAdmin admin, CancellationToken ct) =>
        {
            if (ApiResults.Invalid(request) is { } invalid)
            {
                return invalid;
            }
            var result = await admin.RetireAsync(id, request.LastDay!.Value, http.User.UserId()!.Value, ct);
            return result.Error is { } error ? ApiResults.Problem(error) : Results.Ok(result.Value);
        });

        return api;
    }
}
