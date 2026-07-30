using System.Net;

namespace Solo.Api;

/// <summary>
/// Which peers may set X-Forwarded-For. Solo's deployment shape is "reverse
/// proxy in front, app on a container or LAN network", so the proxy always
/// reaches the app from a loopback or private address — and a request arriving
/// straight from a public address is by definition not that proxy, so its
/// forwarding headers are somebody's claim about themselves.
/// </summary>
public static class TrustedProxies
{
    /// <summary>
    /// Loopback plus the private and link-local ranges of both address
    /// families: RFC 1918, RFC 4193 unique-local, and RFC 3927 / RFC 4291
    /// link-local.
    /// </summary>
    public static IReadOnlyList<IPNetwork> Networks { get; } =
    [
        new(IPAddress.Parse("127.0.0.0"), 8),
        new(IPAddress.Parse("10.0.0.0"), 8),
        new(IPAddress.Parse("172.16.0.0"), 12),
        new(IPAddress.Parse("192.168.0.0"), 16),
        new(IPAddress.Parse("169.254.0.0"), 16),
        new(IPAddress.IPv6Loopback, 128),
        new(IPAddress.Parse("fc00::"), 7),
        new(IPAddress.Parse("fe80::"), 10),
    ];

    /// <summary>
    /// Extra proxy addresses named in configuration, for a deployment whose
    /// proxy does sit on a public address. Unparseable entries are skipped
    /// rather than failing startup over a stray comma.
    /// </summary>
    public static IEnumerable<IPAddress> Configured(IConfiguration config) =>
        (config["ForwardedHeaders:KnownProxies"] ?? string.Empty)
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(value => IPAddress.TryParse(value, out var address) ? address : null)
        .OfType<IPAddress>();

    /// <summary>
    /// True when an address is one the server should never be talked into
    /// fetching from — loopback, private, link-local, or otherwise not a
    /// public destination. Used to keep the avatar proxy off the internal
    /// network.
    /// </summary>
    public static bool IsPrivate(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal ||
            address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal ||
            address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            return true;
        }

        // An IPv4 address tunnelled through IPv6 is still that IPv4 address.
        if (address.IsIPv4MappedToIPv6)
        {
            return IsPrivate(address.MapToIPv4());
        }

        return Networks.Any(network => network.Contains(address));
    }
}
