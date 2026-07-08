using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.JSInterop;

namespace Solo.Client.Auth;

/// <summary>
/// Supplies the OIDC <em>ID token</em> for API calls and the SignalR handshake.
///
/// The API validates bearer tokens with <c>ValidAudience = ClientId</c>, which is
/// the ID token's audience — and Google's access tokens are opaque strings, not
/// JWTs, so they could never validate. The ID token is the only thing that works.
///
/// Where it comes from depends on the provider, and only this class needs to know:
/// <list type="bullet">
/// <item>Google Identity Services hands the ID token over directly.</item>
/// <item>For code + PKCE providers, Blazor's <see cref="IAccessTokenProvider"/> only
/// ever exposes the access token, so we read the ID token out of the session store
/// oidc-client writes. Requesting an access token first is what triggers a silent
/// renewal, so the stored ID token we then read is fresh.</item>
/// </list>
/// </summary>
public sealed class IdTokenProvider(
    IServiceProvider services,
    IJSRuntime js,
    AppConfig config,
    GoogleIdentityService google)
{
    public async ValueTask<string?> GetTokenAsync()
    {
        if (config.IsGoogle)
        {
            return await google.GetTokenAsync();
        }

        // Absent when the instance has no OIDC provider configured yet (pre-setup).
        if (services.GetService(typeof(IAccessTokenProvider)) is not IAccessTokenProvider accessTokens)
        {
            return null;
        }

        try
        {
            await accessTokens.RequestAccessToken();
        }
        catch (AccessTokenNotAvailableException)
        {
            // Not signed in, or the session can't be renewed silently. Either way
            // there is no token to attach; the API answers 401 and the router
            // sends the user to sign in.
            return null;
        }

        return await js.InvokeAsync<string?>("solo.auth.idToken");
    }
}
