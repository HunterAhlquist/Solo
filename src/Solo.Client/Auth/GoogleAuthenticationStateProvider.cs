using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace Solo.Client.Auth;

/// <summary>
/// Derives the authentication state from the stored credentials. The claims are
/// for display only — the API re-validates every token's signature, issuer and
/// audience on each request, and is the sole authority on who you are.
/// </summary>
public sealed class GoogleAuthenticationStateProvider : AuthenticationStateProvider, IDisposable
{
    private static readonly AuthenticationState Anonymous =
        new(new ClaimsPrincipal(new ClaimsIdentity()));

    private readonly GoogleIdentityService _google;

    public GoogleAuthenticationStateProvider(GoogleIdentityService google)
    {
        _google = google;
        _google.SignedInChanged += OnSignedInChanged;
    }

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        var (signedIn, display) = await _google.GetAuthStateAsync();
        if (!signedIn)
        {
            return Anonymous;
        }

        if (display is not null)
        {
            return new AuthenticationState(display);
        }

        // Desktop edge: a live session whose stored profile is missing or
        // unreadable. Still signed in — the API honours the session token —
        // just without a name to show.
        return new AuthenticationState(new ClaimsPrincipal(
            new ClaimsIdentity([], authenticationType: "solo-session", nameType: "name", roleType: "role")));
    }

    private void OnSignedInChanged() =>
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());

    public void Dispose() => _google.SignedInChanged -= OnSignedInChanged;
}
