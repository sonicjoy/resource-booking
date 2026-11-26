using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace ResourceBooking.Auth;

/// <summary>Request-level protections for /api that sit outside any one endpoint.</summary>
public static class ApiGuards
{
    public const string ReseedLimit = "reseed";

    public static IServiceCollection AddApiGuards(this IServiceCollection services) =>
        services.AddRateLimiter(options =>
        {
            // One global window: anyone can be an admin on the demo, so a per-user limit would not hold.
            options.AddFixedWindowLimiter(ReseedLimit, window =>
            {
                window.PermitLimit = 1;
                window.Window = TimeSpan.FromSeconds(30);
                window.QueueLimit = 0;
            });
            options.OnRejected = (context, ct) =>
            {
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                return new ValueTask(context.HttpContext.Response.WriteAsJsonAsync(
                    new { code = "RATE_LIMITED", message = "Try again in a moment" }, ct));
            };
        });

    /// <summary>
    /// Refuses a state-changing /api request sent from another site. SameSite=Strict already keeps
    /// the cookie off such requests; this also covers browsers and requests that send no cookie rules.
    /// A request with no Origin header (curl, server-to-server) is allowed.
    /// </summary>
    public static IApplicationBuilder UseSameOriginForApi(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var request = context.Request;
            var unsafeMethod = !(HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) || HttpMethods.IsOptions(request.Method));
            if (unsafeMethod && request.Path.StartsWithSegments("/api") && request.Headers.Origin is [{ } origin]
                && (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || !string.Equals(uri.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase)))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            await next(context);
        });
}
