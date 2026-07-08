using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace Solo.Client.Auth;

/// <summary>
/// Derives the authentication state from the stored Google ID token. The claims are
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
        var token = await _google.GetTokenAsync();
        return GoogleJwt.ToPrincipal(token) is { } principal
            ? new AuthenticationState(principal)
            : Anonymous;
    }

    private void OnSignedInChanged() =>
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());

    public void Dispose() => _google.SignedInChanged -= OnSignedInChanged;
}
