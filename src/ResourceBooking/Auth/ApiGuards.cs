using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace ResourceBooking.Auth;

/// <summary>Protections for /api that sit outside any one endpoint.</summary>
public static class ApiGuards
{
    public const string ReseedLimit = "reseed";
    public const string AssistantLimit = "assistant";

    public static IServiceCollection AddApiGuards(this IServiceCollection services) =>
        services.AddRateLimiter(options =>
        {
            // One global window: anyone can be an admin on the demo, so a per-user limit would not hold.
            // Demo:ReseedWindowSeconds (default 30) sets it; 0 turns it off, as end-to-end runs need.
            options.AddPolicy(ReseedLimit, context =>
            {
                var seconds = context.RequestServices.GetRequiredService<IConfiguration>().GetValue("Demo:ReseedWindowSeconds", 30);
                return seconds <= 0
                    ? RateLimitPartition.GetNoLimiter("all")
                    : RateLimitPartition.GetFixedWindowLimiter("all", _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 1,
                        Window = TimeSpan.FromSeconds(seconds),
                        QueueLimit = 0,
                    });
            });
            // Per signed-in user: Assistant:MessagesPerMinute (default 10); 0 turns it off.
            options.AddPolicy(AssistantLimit, context =>
            {
                var perMinute = context.RequestServices.GetRequiredService<IConfiguration>().GetValue("Assistant:MessagesPerMinute", 10);
                var user = context.User.UserId()?.ToString() ?? "anonymous";
                return perMinute <= 0
                    ? RateLimitPartition.GetNoLimiter(user)
                    : RateLimitPartition.GetFixedWindowLimiter(user, _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = perMinute,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    });
            });
            options.OnRejected = async (context, ct) =>
            {
                var response = context.HttpContext.Response;
                response.StatusCode = StatusCodes.Status429TooManyRequests;
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
                }
                await Results.Problem(
                    statusCode: StatusCodes.Status429TooManyRequests,
                    title: "Too many requests",
                    extensions: new Dictionary<string, object?> { ["code"] = "RATE_LIMITED", ["message"] = "Try again in a moment" })
                    .ExecuteAsync(context.HttpContext);
            };
        });

    /// <summary>
    /// Refuses a state-changing /api request whose Origin is another site. SameSite=Strict already keeps
    /// the cookie off such requests; this also covers sign-in, which needs no cookie. A request with no
    /// Origin (curl, server-to-server) is allowed.
    /// </summary>
    public static RouteGroupBuilder RequireSameOrigin(this RouteGroupBuilder group) =>
        group.AddEndpointFilter(async (context, next) =>
        {
            var request = context.HttpContext.Request;
            var safeMethod = HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) || HttpMethods.IsOptions(request.Method);
            if (!safeMethod && request.Headers.Origin is [{ } origin] && !IsOwnHost(origin, request))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
            return await next(context);
        });

    private static bool IsOwnHost(string origin, HttpRequest request)
    {
        return Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            && string.Equals(uri.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }
}
