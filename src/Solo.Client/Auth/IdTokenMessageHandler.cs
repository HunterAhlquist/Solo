using System.Net;
using System.Net.Http.Headers;
using Solo.Client.Realtime;
using Solo.Shared.Hubs;

namespace Solo.Client.Auth;

/// <summary>
/// Attaches the bearer credential, if there is one (see <see cref="IdTokenProvider"/>),
/// and stamps the live hub connection id so the server can exclude this client
/// from the change broadcast its own write produces.
/// </summary>
public sealed class IdTokenMessageHandler(
    IdTokenProvider tokens, ConnectionIdAccessor connections, SessionExpiry sessionExpiry)
    : DelegatingHandler
{
    /// <summary>
    /// Without it the server ignores the web app's session cookie, so another
    /// page on the same site can't spend it (Solo.Api's SessionCookie).
    /// </summary>
    public const string ClientHeader = "X-Solo-Client";

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Anonymous endpoints (auth config, setup status) simply get no header.
        bool hadCredential;
        if (await tokens.GetTokenAsync() is { Length: > 0 } token)
        {
            hadCredential = true;
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        else
        {
            hadCredential = await tokens.HoldsSessionCookieAsync();
        }

        request.Headers.TryAddWithoutValidation(ClientHeader, "1");

        if (connections.ConnectionId is { Length: > 0 } connectionId)
        {
            request.Headers.TryAddWithoutValidation(
                BoardHubConstants.ConnectionIdHeader, connectionId);
        }

        var response = await base.SendAsync(request, cancellationToken);

        // A 401 on a request that carried a credential means we thought we were
        // signed in and the server disagreed (revoked, expired).
        // Raise the re-sign-in dialog instead of letting saves fail mysteriously.
        if (hadCredential && response.StatusCode == HttpStatusCode.Unauthorized)
        {
            sessionExpiry.NotifyExpired();
        }

        return response;
    }
}
