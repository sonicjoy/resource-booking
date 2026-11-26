using System.Security.Claims;

namespace ResourceBooking.Auth;

public static class ClaimsPrincipalExtensions
{
    /// <summary>The signed-in user's id, or null when no one is signed in.</summary>
    public static Guid? UserId(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
}
