using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Solo.Client.Auth;

/// <summary>
/// Sign-in via Google Identity Services. The "credential" GIS hands back <em>is</em>
/// the ID token the API validates, so there is no token endpoint, no client secret,
/// and no redirect round trip.
/// </summary>
/// <remarks>
/// Google fixes a GIS credential's life at about an hour and offers no silent-iframe
/// renewal — so this service renews for itself. A timer re-prompts GIS a few minutes
/// before <c>exp</c>, which FedCM answers silently while the user's Google session is
/// alive; a token found expired (say, after the machine slept) gets one renewal
/// attempt before it's retired. Only when renewal fails does the user hear about it:
/// <see cref="SessionExpiry"/> raises the in-place re-sign-in dialog.
/// </remarks>
public sealed class GoogleIdentityService(IJSRuntime js, AppConfig config, SessionExpiry sessionExpiry)
    : IAsyncDisposable
{
    /// <summary>Renew this long before <c>exp</c>, so a request never carries a dying token.</summary>
    private static readonly TimeSpan RenewalLead = TimeSpan.FromMinutes(5);

    private DotNetObjectReference<GoogleIdentityService>? _self;
    private string? _token;
    private bool? _desktop;
    private Task<bool>? _renewal;
    private Timer? _renewalTimer;
    private DateTimeOffset? _renewalScheduledFor;

    /// <summary>Raised when a credential arrives or is cleared.</summary>
    public event Action? SignedInChanged;

    /// <summary>Raised when a desktop browser sign-in could not complete
    /// (instance unreachable, flow expired, or a too-old server).</summary>
    public event Action<string>? SignInFailed;

    /// <summary>
    /// True inside the Solo.Desktop Electron shell or the Solo.Mobile app.
    /// Google refuses to host its sign-in pages in either, so this path never
    /// loads the GIS script:
    /// sign-in happens in the system browser on the instance's own origin,
    /// and the shell delivers back a ready-made Solo session.
    /// </summary>
    public async ValueTask<bool> IsDesktopAsync() =>
        _desktop ??= await js.InvokeAsync<bool>("solo.desktop.available");

    /// <summary>
    /// Wires the credential callback. On the web this loads the GIS script; in
    /// the desktop shell it only registers the .NET receiver — called by the
    /// sign-in UI, and lazily by silent renewal.
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

    /// <summary>Whether the credential persists across browser restarts (localStorage).</summary>
    public ValueTask<bool> GetRememberAsync() => js.InvokeAsync<bool>("solo.google.remember");

    public ValueTask SetRememberAsync(bool value) => js.InvokeVoidAsync("solo.google.setRemember", value);

    /// <summary>Called from JS when a GIS credential lands (web only).</summary>
    [JSInvokable]
    public Task OnCredentialReceived(string credential)
    {
        _token = credential;
        ScheduleRenewal(credential);
        SignedInChanged?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Called from JS when the shell has stored a browser-won Solo session
    /// (desktop only). The session is already live; there is nothing to renew.
    /// </summary>
    [JSInvokable]
    public Task OnDesktopSignedIn()
    {
        SignedInChanged?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>Called from JS when a browser sign-in couldn't be completed.</summary>
    [JSInvokable]
    public Task OnSignInFailed(string message)
    {
        SignInFailed?.Invoke(message);
        return Task.CompletedTask;
    }

    /// <summary>
    /// The bearer credential for API calls: on desktop the long-lived Solo
    /// session token (no client-side expiry — the server slides it); on the
    /// web the stored Google credential, renewed silently if it can be, or
    /// null once it is truly gone. Reads storage directly — deliberately
    /// without loading the GIS script, since this runs on every
    /// authentication-state query; the script is only fetched when a renewal
    /// is actually attempted.
    /// </summary>
    public async ValueTask<string?> GetTokenAsync()
    {
        if (await IsDesktopAsync())
        {
            return await js.InvokeAsync<string?>("solo.session.token");
        }

        _token ??= await js.InvokeAsync<string?>("solo.google.token");

        if (!GoogleJwt.IsExpired(_token))
        {
            ScheduleRenewal(_token!);
            return _token;
        }

        if (_token is null)
        {
            // Never signed in here: nothing to renew, nothing to retire.
            return null;
        }

        // The credential died while we weren't looking (slept laptop, restarted
        // browser with "stay signed in"). One silent re-issue usually saves it.
        if (await TryRenewAsync() && !GoogleJwt.IsExpired(_token))
        {
            return _token;
        }

        await RetireAsync();
        return null;
    }

    /// <summary>
    /// Asks GIS for a fresh credential without user interaction. Single-flight:
    /// concurrent callers (every in-flight request checks the token) share one
    /// prompt rather than stampeding Google.
    /// </summary>
    public async Task<bool> TryRenewAsync()
    {
        if (!config.IsGoogle)
        {
            return false;
        }

        if (_renewal is { } pending)
        {
            return await pending;
        }

        var attempt = RenewCoreAsync();
        _renewal = attempt;
        try
        {
            return await attempt;
        }
        finally
        {
            _renewal = null;
        }
    }

    private async Task<bool> RenewCoreAsync()
    {
        try
        {
            if (await IsDesktopAsync())
            {
                // No silent channel in the shell: GIS (and its FedCM prompt)
                // can't run there. Fail fast so the re-sign-in dialog appears
                // immediately; one click sends the user through the browser.
                return false;
            }

            await InitializeAsync();
            var credential = await js.InvokeAsync<string?>("solo.google.prompt");
            if (GoogleJwt.IsExpired(credential))
            {
                return false;
            }

            // OnCredentialReceived also lands the token, but interop ordering
            // between the JS callback and this resolution isn't guaranteed —
            // take the credential from the prompt itself.
            _token = credential;
            ScheduleRenewal(credential!);
            return true;
        }
        catch (JSException)
        {
            // GIS script blocked or unreachable: treat as a failed renewal.
            return false;
        }
    }

    /// <summary>
    /// Arms a one-shot renewal a little before this token's <c>exp</c>. Idempotent
    /// per token, so the per-request token reads don't re-arm it constantly.
    /// </summary>
    private void ScheduleRenewal(string token)
    {
        if (!config.IsGoogle || GoogleJwt.Expiry(token) is not { } expiry || _renewalScheduledFor == expiry)
        {
            return;
        }

        var due = expiry - RenewalLead - DateTimeOffset.UtcNow;
        if (due < TimeSpan.FromSeconds(1))
        {
            due = TimeSpan.FromSeconds(1);
        }

        _renewalScheduledFor = expiry;
        _renewalTimer?.Dispose();
        _renewalTimer = new Timer(_ => _ = OnRenewalDueAsync(), null, due, Timeout.InfiniteTimeSpan);
    }

    private async Task OnRenewalDueAsync()
    {
        try
        {
            if (!await TryRenewAsync())
            {
                sessionExpiry.NotifyExpired();
            }
        }
        catch
        {
            // A timer callback must never throw; the dialog is the fallback.
            sessionExpiry.NotifyExpired();
        }
    }

    /// <summary>
    /// Drops a dead credential without touching GIS auto-select — expiring is not
    /// signing out, and the next sign-in should still be able to happen silently.
    /// </summary>
    private async Task RetireAsync()
    {
        _token = null;
        CancelScheduledRenewal();
        await js.InvokeVoidAsync("solo.google.retire");
        SignedInChanged?.Invoke();
    }

    /// <summary>
    /// Whether the user counts as signed in, and the display-only claims that
    /// say who they are. On the web both come from the Google credential; on
    /// desktop the session token authenticates while the profile the instance
    /// delivered alongside it names the user.
    /// </summary>
    public async ValueTask<(bool SignedIn, ClaimsPrincipal? Display)> GetAuthStateAsync()
    {
        if (!await IsDesktopAsync())
        {
            var token = await GetTokenAsync();
            return (token is not null, GoogleJwt.ToPrincipal(token));
        }

        if (await GetTokenAsync() is null)
        {
            return (false, null);
        }

        return (true, DesktopUser.ToPrincipal(await js.InvokeAsync<string?>("solo.session.user")));
    }

    public async Task SignOutAsync()
    {
        _token = null;
        CancelScheduledRenewal();
        if (await IsDesktopAsync())
        {
            await js.InvokeVoidAsync("solo.session.revoke");
        }

        await js.InvokeVoidAsync("solo.google.signOut");
        SignedInChanged?.Invoke();
    }

    private void CancelScheduledRenewal()
    {
        _renewalTimer?.Dispose();
        _renewalTimer = null;
        _renewalScheduledFor = null;
    }

    public ValueTask DisposeAsync()
    {
        CancelScheduledRenewal();
        _self?.Dispose();
        return ValueTask.CompletedTask;
    }
}
