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
    }

    public Uri BaseAddress { get; }

    public Uri Resolve(string relativePath) => new(BaseAddress, relativePath.TrimStart('/'));

    public Uri HubUri => Resolve(BoardHubConstants.Path);
}
