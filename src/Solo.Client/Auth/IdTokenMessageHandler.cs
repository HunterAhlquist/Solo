using System.Net.Http.Headers;
using Solo.Client.Realtime;
using Solo.Shared.Hubs;

namespace Solo.Client.Auth;

/// <summary>
/// Attaches the ID token as the bearer credential (see <see cref="IdTokenProvider"/>)
/// and stamps the live hub connection id so the server can exclude this client
/// from the change broadcast its own write produces.
/// </summary>
public sealed class IdTokenMessageHandler(IdTokenProvider tokens, ConnectionIdAccessor connections)
    : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Anonymous endpoints (auth config, setup status) simply get no header.
        if (await tokens.GetTokenAsync() is { Length: > 0 } token)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (connections.ConnectionId is { Length: > 0 } connectionId)
        {
            request.Headers.TryAddWithoutValidation(
                BoardHubConstants.ConnectionIdHeader, connectionId);
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
