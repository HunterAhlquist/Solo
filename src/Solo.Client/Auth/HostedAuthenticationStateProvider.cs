using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;

namespace Solo.Client.Auth;

/// <summary>
/// The authentication state inside an app that hosts the client
/// (<see cref="Hosting.HostShell"/>): signed in exactly when the app handed a
/// session over, as whoever the profile beside it says. The provider plays no
/// part — the app did the signing in, and the API validates the session token
/// on every request.
/// </summary>
public sealed class HostedAuthenticationStateProvider(IJSRuntime js) : AuthenticationStateProvider
{
    private static readonly AuthenticationState Anonymous = new(new ClaimsPrincipal(new ClaimsIdentity()));

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        if (await js.InvokeAsync<string?>("solo.session.token") is not { Length: > 0 })
        {
            return Anonymous;
        }

        // The profile is for display only; a session without a readable one is still a session.
        return new AuthenticationState(
            DesktopUser.ToPrincipal(await js.InvokeAsync<string?>("solo.session.user"))
            ?? new ClaimsPrincipal(new ClaimsIdentity([], authenticationType: "solo-session", nameType: "name", roleType: "role")));
    }
}
