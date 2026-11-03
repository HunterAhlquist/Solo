using System.Net;
using Solo.Api;
using Xunit;

namespace Solo.Api.Tests;

/// <summary>
/// Two things lean on this classification: which peers may set
/// X-Forwarded-For (spoofing it hands the caller a fresh rate-limit
/// partition, and the auth endpoints are what the limit protects), and where
/// the avatar proxy is allowed to connect.
/// </summary>
public class TrustedProxiesTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.10.20.30")]
    [InlineData("10.0.0.4")]
    [InlineData("10.255.255.255")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.254")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")] // Cloud metadata: the classic SSRF target.
    [InlineData("::1")]
    [InlineData("fd00::1")]
    [InlineData("fe80::1")]
    [InlineData("::ffff:10.0.0.4")] // The same private address, tunnelled.
    [InlineData("0.0.0.0")]
    [InlineData("100.64.0.1")] // RFC 6598 shared space: carrier NAT, Tailscale.
    [InlineData("100.127.255.254")]
    [InlineData("::ffff:100.100.1.1")]
    [InlineData("192.0.0.8")]
    [InlineData("198.18.0.1")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("ff02::1")]
    public void Internal_addresses_are_recognised(string address)
    {
        Assert.True(TrustedProxies.IsPrivate(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("172.32.0.1")] // Just outside the /12.
    [InlineData("172.15.255.255")]
    [InlineData("192.169.0.1")]
    [InlineData("11.0.0.1")]
    [InlineData("100.63.255.255")] // Just outside the /10.
    [InlineData("100.128.0.0")]
    [InlineData("192.0.1.1")]
    [InlineData("198.17.255.255")]
    [InlineData("2606:4700::1111")]
    public void Public_addresses_are_not(string address)
    {
        Assert.False(TrustedProxies.IsPrivate(IPAddress.Parse(address)));
    }

    // The fetch guard refuses more than the forwarded-header trust admits: a
    // Tailscale peer at 100.x must not be fetched from, but is not a reverse
    // proxy whose X-Forwarded-For should be believed either.
    [Theory]
    [InlineData("100.64.0.1")]
    [InlineData("224.0.0.1")]
    public void Shared_and_reserved_space_is_not_a_trusted_proxy(string address)
    {
        var ip = IPAddress.Parse(address);
        Assert.True(TrustedProxies.IsPrivate(ip));
        Assert.DoesNotContain(TrustedProxies.Networks, network => network.Contains(ip));
    }
}
