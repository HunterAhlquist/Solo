using Microsoft.JSInterop;

namespace Solo.Client.Hosting;

/// <summary>
/// Whether the client is running inside an app that shows it in a web view of
/// its own — Solo's iOS app opening an extension's page, which it has no
/// native screen for. Such an app puts <c>window.soloHost</c> in the page
/// before any script runs, and a session beside it.
///
/// <para>Hosted, the client is one page and nothing around it: the app draws
/// the bars and the navigation, so the layout leaves them out; the app is
/// already signed in, so the session it handed over is the credential whatever
/// the instance's provider is, and a session the API refuses is the app's to
/// renew; and a link to a screen the app has of its own goes to the app.</para>
///
/// <para>The page is given nothing of the device it runs on. What it can say
/// to the app is listed here and nowhere else: that it is up, that its session
/// was refused, and where it would like to go.</para>
/// </summary>
public sealed class HostShell(bool hosted)
{
    public bool IsHosted { get; } = hosted;

    /// <summary>Asked once, at boot, before the services that depend on the answer are registered.</summary>
    public static async Task<HostShell> DetectAsync(IJSRuntime js)
    {
        try
        {
            return new HostShell(await js.InvokeAsync<bool>("solo.host.available"));
        }
        catch (JSException)
        {
            return new HostShell(false);
        }
    }

    /// <summary>The first page has rendered: the app can stop showing that it is waiting.</summary>
    public ValueTask ReadyAsync(IJSRuntime js) => PostAsync(js, "ready");

    /// <summary>The API refused the session the app handed over. Signing in again is the app's to do.</summary>
    public ValueTask SessionRefusedAsync(IJSRuntime js) => PostAsync(js, "sessionRefused");

    /// <summary>
    /// Whether the app has a screen of its own for a path, in which case it
    /// has been asked to go there and the page should stay where it is.
    /// </summary>
    public async ValueTask<bool> ClaimsAsync(IJSRuntime js, string path)
    {
        if (!IsHosted)
        {
            return false;
        }

        try
        {
            return await js.InvokeAsync<bool>("solo.host.claim", path);
        }
        catch (JSException)
        {
            return false;
        }
    }

    private async ValueTask PostAsync(IJSRuntime js, string type)
    {
        if (!IsHosted)
        {
            return;
        }

        try
        {
            await js.InvokeVoidAsync("solo.host.post", type);
        }
        catch (JSException)
        {
            // The app has gone, or never listened: nothing here depends on it having heard.
        }
    }
}
