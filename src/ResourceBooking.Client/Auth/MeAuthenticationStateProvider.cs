using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using ResourceBooking.Shared.Dtos;

namespace ResourceBooking.Client.Auth;

/// <summary>
/// The browser can't read the HttpOnly session cookie, so it asks the server who is signed in.
/// </summary>
public sealed class MeAuthenticationStateProvider(HttpClient http) : AuthenticationStateProvider
{
    private static readonly AuthenticationState SignedOut = new(new ClaimsPrincipal(new ClaimsIdentity()));

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        CurrentUser? me;
        try
        {
            using var response = await http.GetAsync("api/auth/me");
            me = response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<CurrentUser>() : null;
        }
        catch (HttpRequestException)
        {
            // Server unreachable: show the app as signed out rather than crash.
            me = null;
        }
        if (me is null)
        {
            return SignedOut;
        }

        return new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, me.Id.ToString()),
                new Claim(ClaimTypes.Name, me.DisplayName),
                new Claim(ClaimTypes.Email, me.Email),
                new Claim(ClaimTypes.Role, me.Role.ToString()),
            ],
            authenticationType: "session")));
    }

    /// <summary>Call after signing in or out so the UI re-reads who is signed in.</summary>
    public void Refresh() => NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
}
