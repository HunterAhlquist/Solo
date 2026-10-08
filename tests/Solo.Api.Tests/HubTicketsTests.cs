using Microsoft.AspNetCore.Http;
using Solo.Api.Auth;
using Xunit;

namespace Solo.Api.Tests;

/// <summary>
/// The hub's URL is written to proxy access logs, so what goes there has to be
/// worthless by the time anyone reads one. ProviderTokenTests covers the same
/// rules through a running host; these pin the edges with a clock in hand.
/// </summary>
public sealed class HubTicketsTests
{
    private static readonly HubTickets.Holder Someone = new("https://issuer", "sub", "a@example.com", "A", null);

    private readonly ManualClock _clock = new();
    private readonly HubTickets _tickets;

    public HubTicketsTests() => _tickets = new HubTickets(_clock);

    [Fact]
    public void A_ticket_in_a_url_is_spent_by_its_first_use()
    {
        var ticket = _tickets.Issue(Someone);

        Assert.Equal(Someone, _tickets.Redeem(ticket, fromUrl: true));
        Assert.Null(_tickets.Redeem(ticket, fromUrl: true));
        Assert.Null(_tickets.Redeem(ticket, fromUrl: false));
    }

    /// <summary>
    /// The client sends it in the header to negotiate, and then on every long
    /// poll; only the URL (the WebSocket connect) spends it.
    /// </summary>
    [Fact]
    public void A_ticket_in_a_header_survives_until_it_expires()
    {
        var ticket = _tickets.Issue(Someone);

        Assert.Equal(Someone, _tickets.Redeem(ticket, fromUrl: false));
        Assert.Equal(Someone, _tickets.Redeem(ticket, fromUrl: false));
        Assert.Equal(Someone, _tickets.Redeem(ticket, fromUrl: true));
    }

    [Fact]
    public void A_ticket_expires()
    {
        var ticket = _tickets.Issue(Someone);
        _clock.Advance(HubTickets.Lifetime);

        Assert.Null(_tickets.Redeem(ticket, fromUrl: false));
        Assert.Null(_tickets.Redeem(ticket, fromUrl: true));
    }

    [Fact]
    public void An_unknown_ticket_is_refused()
    {
        Assert.Null(_tickets.Redeem(HubTickets.Prefix + "made-up", fromUrl: true));
    }

    [Theory]
    [InlineData("/hubs/board", true)]
    [InlineData("/hubs/board/negotiate", true)]
    [InlineData("/api/settings", false)]
    [InlineData("/hubs/boardroom", false)]
    public void Only_the_hub_path_reads_a_ticket(string path, bool read)
    {
        var request = new DefaultHttpContext().Request;
        request.Path = path;
        request.QueryString = new QueryString($"?access_token={HubTickets.Prefix}abc");

        Assert.Equal(read, HubTickets.FromRequest(request) is not null);
    }

    [Fact]
    public void Where_the_ticket_came_from_is_reported()
    {
        var url = new DefaultHttpContext().Request;
        url.Path = "/hubs/board";
        url.QueryString = new QueryString($"?access_token={HubTickets.Prefix}abc");

        var header = new DefaultHttpContext().Request;
        header.Path = "/hubs/board";
        header.Headers.Authorization = $"Bearer {HubTickets.Prefix}abc";

        Assert.Equal(($"{HubTickets.Prefix}abc", true), HubTickets.FromRequest(url));
        Assert.Equal(($"{HubTickets.Prefix}abc", false), HubTickets.FromRequest(header));
    }

    /// <summary>A long-lived credential on the hub URL is not a ticket, so nothing reads it.</summary>
    [Theory]
    [InlineData("solo_session-token")]
    [InlineData("eyJhbGciOiJSUzI1NiJ9.e30.sig")]
    public void Other_credentials_in_the_url_are_not_tickets(string token)
    {
        var request = new DefaultHttpContext().Request;
        request.Path = "/hubs/board";
        request.QueryString = new QueryString($"?access_token={token}");

        Assert.Null(HubTickets.FromRequest(request));
        Assert.Null(SessionTokens.FromRequest(request));
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
