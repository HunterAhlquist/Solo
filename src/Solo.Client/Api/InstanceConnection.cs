using Microsoft.JSInterop;
using Solo.Shared.Hubs;

namespace Solo.Client.Api;

/// <summary>
/// The single place that knows where the Solo instance lives. The hosted web
/// build points this at the page origin; a future Electron shell points it at a
/// self-hosted address instead — exactly one instance at a time. Nothing else
/// in the client may assume same-origin: REST calls, the SignalR hub, and the
/// anonymous auth-config fetch all resolve their URLs here.
/// </summary>
public sealed class InstanceConnection
{
    public InstanceConnection(string baseAddress)
    {
        // A trailing slash makes relative Uri composition behave: without it,
        // the last path segment of the base is discarded.
        BaseAddress = new Uri(baseAddress.EndsWith('/') ? baseAddress : baseAddress + '/');
        // Overwritten at startup when the desktop shell answers; see below.
        ExternalAddress = BaseAddress;
    }

    public Uri BaseAddress { get; }

    /// <summary>
    /// Where something <em>outside</em> this browser must go to reach the
    /// instance. Equal to <see cref="BaseAddress"/> on the web, where the page
    /// origin is the instance.
    /// </summary>
    /// <remarks>
    /// The desktop shell is the reason this is a separate address at all: it
    /// serves the client from <c>http://localhost:41321</c> and proxies the API
    /// onward, so <see cref="BaseAddress"/> is a local server that exists only
    /// while the app is running. Printing it in an MCP client's config, or
    /// anywhere else a user pastes an address into another tool, produces
    /// something that cannot work.
    /// </remarks>
    public Uri ExternalAddress { get; private set; }

    public Uri Resolve(string relativePath) => new(BaseAddress, relativePath.TrimStart('/'));

    /// <summary>As <see cref="Resolve"/>, but against <see cref="ExternalAddress"/>.</summary>
    public Uri ResolveExternal(string relativePath) => new(ExternalAddress, relativePath.TrimStart('/'));

    public Uri HubUri => Resolve(BoardHubConstants.Path);

    /// <summary>
    /// Asks the desktop shell which instance it is pointed at. A no-op on the
    /// web build, where the bridge is absent and the page origin is already the
    /// answer.
    /// </summary>
    /// <remarks>
    /// Called once at startup, before the first render, rather than by whichever
    /// page happens to need it — the answer cannot change without a restart, and
    /// a page should not have to know the shell exists.
    /// </remarks>
    public async Task ResolveExternalAddressAsync(IJSRuntime js)
    {
        try
        {
            var configured = await js.InvokeAsync<string?>("solo.desktop.instanceUrl");
            if (!string.IsNullOrWhiteSpace(configured) &&
                Uri.TryCreate(configured, UriKind.Absolute, out var address))
            {
                ExternalAddress = WithTrailingSlash(address);
            }
        }
        catch (JSException)
        {
            // No bridge, or an older shell that does not answer: the page origin
            // stays the best answer available.
        }
    }

    private static Uri WithTrailingSlash(Uri address) =>
        address.AbsoluteUri.EndsWith('/') ? address : new Uri(address.AbsoluteUri + '/');
}
