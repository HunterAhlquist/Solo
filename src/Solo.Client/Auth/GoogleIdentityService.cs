using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Solo.Client.Auth;

/// <summary>
/// Sign-in via Google Identity Services. The "credential" GIS hands back <em>is</em>
/// an ID token the API validates, so there is no token endpoint, no client secret,
/// and no redirect round trip.
/// </summary>
/// <remarks>
/// The credential proves who signed in, once. Google fixes its life at about an
/// hour and issues nothing to refresh it with, so it is traded straight away for a
/// Solo session that slides on use: an HttpOnly cookie on the web, a bearer token
/// in the desktop shell. Nothing on the web stores the credential afterwards. When
/// the session dies (revoked, idle past its lifetime) the API answers 401 and
/// <see cref="SessionExpiry"/> raises the in-place re-sign-in dialog.
/// </remarks>
public sealed class GoogleIdentityService(IJSRuntime js, AppConfig config) : IAsyncDisposable
{
    private DotNetObjectReference<GoogleIdentityService>? _self;
    private bool? _desktop;

    // A credential that couldn't buy a session, held for this page only. It
    // happens for an identity nobody invited: the API then answers with the
    // access-denied notice rather than a bare 401.
    private string? _token;

    /// <summary>Raised when a credential arrives or is cleared.</summary>
    public event Action? SignedInChanged;

    /// <summary>Raised when a desktop browser sign-in could not complete
    /// (instance unreachable, flow expired, or a too-old server).</summary>
    public event Action<string>? SignInFailed;

    /// <summary>
    /// True inside the Solo.Desktop Electron shell. Google refuses to host its
    /// sign-in pages there, so the desktop path never loads the GIS script:
    /// sign-in happens in the system browser on the instance's own origin,
    /// and the shell delivers back a ready-made Solo session.
    /// </summary>
    public async ValueTask<bool> IsDesktopAsync() =>
        _desktop ??= await js.InvokeAsync<bool>("solo.desktop.available");

    /// <summary>
    /// Wires the credential callback. On the web this loads the GIS script; in
    /// the desktop shell it only registers the .NET receiver. Called by the
    /// sign-in UI.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_self is not null || !config.IsGoogle)
        {
            return;
        }

        _self = DotNetObjectReference.Create(this);
        if (await IsDesktopAsync())
        {
            await js.InvokeVoidAsync("solo.desktop.init", _self);
        }
        else
        {
            await js.InvokeVoidAsync("solo.google.init", config.Auth!.ClientId, _self);
        }
    }

    /// <summary>
    /// Desktop only: opens the browser sign-in flow. Registers the callback
    /// first, so the credential can land the moment the browser completes.
    /// </summary>
    public async Task BeginBrowserSignInAsync()
    {
        await InitializeAsync();
        await js.InvokeVoidAsync("solo.desktop.beginSignIn");
    }

    public ValueTask RenderButtonAsync(ElementReference element, bool darkMode) =>
        js.InvokeVoidAsync("solo.google.renderButton", element, darkMode ? "filled_black" : "outline");

    /// <summary>Whether the session outlives a browser restart.</summary>
    public ValueTask<bool> GetRememberAsync() => js.InvokeAsync<bool>("solo.google.remember");

    public ValueTask SetRememberAsync(bool value) => js.InvokeVoidAsync("solo.google.setRemember", value);

    /// <summary>Called from JS when a GIS credential lands (web only).</summary>
    [JSInvokable]
    public async Task OnCredentialReceived(string credential)
    {
        try{await js.InvokeVoidAsync("solo.offline.credential",GoogleJwt.ToPrincipal(credential)?.FindFirst("email")?.Value);}catch(JSException){}
        var user = await js.InvokeAsync<string?>("solo.session.begin", credential);
        _token = user is null ? credential : null;
        SignedInChanged?.Invoke();
    }

    /// <summary>
    /// Called from JS when the shell has stored a browser-won Solo session
    /// (desktop only).
    /// </summary>
    [JSInvokable]
    public async Task OnDesktopSignedIn()
    {
        await OfflineLock();
        SignedInChanged?.Invoke();
    }

    /// <summary>Called from JS when a browser sign-in couldn't be completed.</summary>
    [JSInvokable]
    public Task OnSignInFailed(string message)
    {
        SignInFailed?.Invoke(message);
        return Task.CompletedTask;
    }

    /// <summary>
    /// The bearer credential for API calls: on desktop the Solo session token;
    /// on the web none, because the session cookie travels by itself, unless a
    /// credential is being held that couldn't buy one.
    /// </summary>
    public async ValueTask<string?> GetTokenAsync()
    {
        if (await IsDesktopAsync())
        {
            return await js.InvokeAsync<string?>("solo.session.token");
        }

        return GoogleJwt.IsExpired(_token) ? null : _token;
    }

    /// <summary>
    /// Whether the web app believes it holds a session cookie. The page can't
    /// read the cookie, so the profile stored beside it stands in.
    /// </summary>
    public async ValueTask<bool> HoldsSessionCookieAsync() =>
        !await IsDesktopAsync() && await js.InvokeAsync<string?>("solo.session.user") is not null;

    /// <summary>Signed in as far as this page knows, without asking the server.</summary>
    public async ValueTask<bool> IsSignedInAsync() =>
        await GetTokenAsync() is not null || await HoldsSessionCookieAsync();

    /// <summary>
    /// Whether the user counts as signed in, and the display-only claims that
    /// say who they are, from the profile the instance delivered with the
    /// session.
    /// </summary>
    public async ValueTask<(bool SignedIn, ClaimsPrincipal? Display)> GetAuthStateAsync()
    {
        if (await IsDesktopAsync())
        {
            if (await GetTokenAsync() is null)
            {
                return (false, null);
            }

            return (true, DesktopUser.ToPrincipal(await js.InvokeAsync<string?>("solo.session.user")));
        }

        if (await js.InvokeAsync<string?>("solo.session.user") is { } profile)
        {
            return (true, DesktopUser.ToPrincipal(profile));
        }

        if (await GetTokenAsync() is { } token)
        {
            return (true, GoogleJwt.ToPrincipal(token));
        }

        if(await IsOffline()){var offline=await js.InvokeAsync<string?>("solo.offline.profile");return (offline is not null,DesktopUser.ToPrincipal(offline));}

        return await ResumeAsync() is { } resumed ? (true, DesktopUser.ToPrincipal(resumed)) : (false, null);
    }

    /// <summary>
    /// A session this page doesn't know it has, as the profile JSON, or null.
    /// </summary>
    private async Task<string?> ResumeAsync()
    {
        // Sign-ins from before sessions left the Google credential in storage.
        // One still alive buys a session; any other is just dropped.
        if (await js.InvokeAsync<string?>("solo.google.token") is { } stored)
        {
            await js.InvokeVoidAsync("solo.google.retire");
            if (!GoogleJwt.IsExpired(stored) && await js.InvokeAsync<string?>("solo.session.begin", stored) is { } user)
            {
                return user;
            }
        }

        // The cookie can outlive the stored profile: Safari clears storage that
        // scripts can write after seven days without a visit, but leaves an
        // HttpOnly cookie the server set. Ask the server whether it still counts.
        return await js.InvokeAsync<string?>("solo.session.probe");
    }

    public async Task SignOutAsync()
    {
        await OfflineLock();
        _token = null;
        await js.InvokeVoidAsync("solo.session.revoke");
        await js.InvokeVoidAsync("solo.google.signOut");
        SignedInChanged?.Invoke();
    }

    private async Task<bool> IsOffline(){try{return await js.InvokeAsync<bool>("solo.offline.isOffline");}catch(JSException){return false;}}
    private async Task OfflineLock(){try{await js.InvokeVoidAsync("solo.offline.lock");}catch(JSException){}}

    public ValueTask DisposeAsync()
    {
        _self?.Dispose();
        return ValueTask.CompletedTask;
    }
}
