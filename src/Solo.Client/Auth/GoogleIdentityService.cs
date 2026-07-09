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
    private Task<bool>? _renewal;
    private Timer? _renewalTimer;
    private DateTimeOffset? _renewalScheduledFor;

    /// <summary>Raised when a credential arrives or is cleared.</summary>
    public event Action? SignedInChanged;

    /// <summary>
    /// Loads the GIS script and wires its callback. Called by the sign-in UI, and
    /// lazily by silent renewal — everything else reads storage without the script.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_self is not null || !config.IsGoogle)
        {
            return;
        }

        _self = DotNetObjectReference.Create(this);
        await js.InvokeVoidAsync("solo.google.init", config.Auth!.ClientId, _self);
    }

    public ValueTask RenderButtonAsync(ElementReference element, bool darkMode) =>
        js.InvokeVoidAsync("solo.google.renderButton", element, darkMode ? "filled_black" : "outline");

    /// <summary>Whether the credential persists across browser restarts (localStorage).</summary>
    public ValueTask<bool> GetRememberAsync() => js.InvokeAsync<bool>("solo.google.remember");

    public ValueTask SetRememberAsync(bool value) => js.InvokeVoidAsync("solo.google.setRemember", value);

    /// <summary>Called from JS when the user completes the Google prompt.</summary>
    [JSInvokable]
    public Task OnCredentialReceived(string credential)
    {
        _token = credential;
        ScheduleRenewal(credential);
        SignedInChanged?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>
    /// The stored credential, renewed silently if it can be, or null once it is
    /// truly gone. Reads session storage directly — deliberately without loading
    /// the GIS script, since this runs on every authentication-state query; the
    /// script is only fetched when a renewal is actually attempted.
    /// </summary>
    public async ValueTask<string?> GetTokenAsync()
    {
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

    public async Task SignOutAsync()
    {
        _token = null;
        CancelScheduledRenewal();
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
