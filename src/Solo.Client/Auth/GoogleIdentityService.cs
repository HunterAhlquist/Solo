using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Solo.Client.Auth;

/// <summary>
/// Sign-in via Google Identity Services. The "credential" GIS hands back <em>is</em>
/// the ID token the API validates, so there is no token endpoint, no client secret,
/// and no redirect round trip.
/// </summary>
/// <remarks>
/// The trade-off, recorded honestly: a GIS credential lives about an hour and there
/// is no silent-iframe renewal. When it expires the token provider stops sending it,
/// the API answers 401, and the route guard lands the user on the sign-in page —
/// where <c>auto_select</c> usually signs them straight back in.
/// </remarks>
public sealed class GoogleIdentityService(IJSRuntime js, AppConfig config) : IAsyncDisposable
{
    private DotNetObjectReference<GoogleIdentityService>? _self;
    private string? _token;

    /// <summary>Raised when a credential arrives or is cleared.</summary>
    public event Action? SignedInChanged;

    /// <summary>Loads the GIS script and wires its callback. Only the sign-in UI needs this.</summary>
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

    /// <summary>Called from JS when the user completes the Google prompt.</summary>
    [JSInvokable]
    public Task OnCredentialReceived(string credential)
    {
        _token = credential;
        SignedInChanged?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>
    /// The stored credential, or null once it expires. Reads session storage
    /// directly — deliberately without loading the GIS script, since this runs on
    /// every authentication-state query.
    /// </summary>
    public async ValueTask<string?> GetTokenAsync()
    {
        _token ??= await js.InvokeAsync<string?>("solo.google.token");

        if (GoogleJwt.IsExpired(_token))
        {
            if (_token is not null)
            {
                await SignOutAsync();
            }

            return null;
        }

        return _token;
    }

    public async Task SignOutAsync()
    {
        _token = null;
        await js.InvokeVoidAsync("solo.google.signOut");
        SignedInChanged?.Invoke();
    }

    public ValueTask DisposeAsync()
    {
        _self?.Dispose();
        return ValueTask.CompletedTask;
    }
}
