using System.Net;
using System.Net.Sockets;

namespace Solo.Api;

/// <summary>
/// An <see cref="SocketsHttpHandler"/> that will only connect to public
/// addresses. For the two places the server fetches a URL it did not choose.
/// </summary>
/// <remarks>
/// <para>Those are the SSO <c>picture</c> claim out of an OIDC token
/// (<c>AvatarConstants</c>) and the release manifests an update check reads —
/// including the feed URL a third-party extension names in its own
/// <c>extension.json</c>. On a self-hosted instance the API can reach the
/// database, the container network and the LAN, so an attacker-chosen URL is a
/// way to ask the server to make requests on its behalf.</para>
///
/// <para>The guard sits on the socket rather than on the URL, and that is the
/// whole point: a hostname is resolved to an address and the <em>address</em>
/// is what gets vetted, so it holds for a DNS name pointing at
/// <c>127.0.0.1</c>, for cloud metadata endpoints, and for redirects — which
/// <see cref="HttpClient"/> follows on its own, with no further chance to
/// inspect the target. Checking the URL instead leaves a window between
/// resolving a name and connecting to it.</para>
/// </remarks>
internal static class PublicOnlyHttpHandler
{
    public static SocketsHttpHandler Create() => new()
    {
        MaxAutomaticRedirections = 5,
        ConnectCallback = async (context, ct) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
            var reachable = addresses.Where(address => !TrustedProxies.IsPrivate(address)).ToArray();
            if (reachable.Length == 0)
            {
                throw new HttpRequestException(
                    $"Refusing to fetch from a non-public address ({context.DnsEndPoint.Host}).");
            }

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(reachable, context.DnsEndPoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };
}
