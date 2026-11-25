using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using ResourceBooking.Data;
using ResourceBooking.Data.Entities;
using ResourceBooking.Shared;

namespace ResourceBooking.Auth;

/// <summary>
/// Cookie sign-in standing in for company SSO. Every API request re-reads the user, so a
/// deactivated, demoted or deleted user's session stops at once.
/// </summary>
public static class CookieSetup
{
    public const string AdminPolicy = "Admin";

    public static IServiceCollection AddAppAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.Name = "rb_session";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.ExpireTimeSpan = TimeSpan.FromHours(8);
                options.SlidingExpiration = true;

                // The client is a WebAssembly app: API calls get status codes, never a redirect.
                options.Events.OnRedirectToLogin = context => Status(context.Response, StatusCodes.Status401Unauthorized);
                options.Events.OnRedirectToAccessDenied = context => Status(context.Response, StatusCodes.Status403Forbidden);
                options.Events.OnValidatePrincipal = ValidateAsync;
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(AdminPolicy, policy => policy.RequireRole(nameof(Role.Admin)));

        // Keys live in PostgreSQL so cookies survive a redeploy.
        services.AddDataProtection().PersistKeysToDbContext<AppDb>();
        return services;
    }

    public static ClaimsPrincipal PrincipalFor(User user) => new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.DisplayName),
            new Claim(ClaimTypes.Role, user.Role.ToString()),
        ],
        CookieAuthenticationDefaults.AuthenticationScheme));

    public static Guid? UserId(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    private static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        // Static files and pages need no user; only API requests pay for the lookup.
        if (!context.Request.Path.StartsWithSegments("/api") || context.Principal is not { } principal)
        {
            return;
        }

        var db = context.HttpContext.RequestServices.GetRequiredService<AppDb>();
        var userId = principal.UserId();
        var user = userId is null ? null : await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.IsActive, u.Role })
            .SingleOrDefaultAsync(context.HttpContext.RequestAborted);

        if (user is null || !user.IsActive || !principal.IsInRole(user.Role.ToString()))
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }
    }

    private static Task Status(HttpResponse response, int status)
    {
        response.StatusCode = status;
        return Task.CompletedTask;
    }
}
