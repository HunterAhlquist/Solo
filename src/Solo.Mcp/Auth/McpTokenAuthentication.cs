using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Solo.Mcp.Data;

namespace Solo.Mcp.Auth;

/// <summary>Names shared by the handler, the middleware and the endpoints.</summary>
public static class McpAuthentication
{
    public const string Scheme = "SoloMcpToken";

    /// <summary>Claim carrying the token id on a principal this extension authenticated.</summary>
    public const string TokenIdClaim = "solo:mcp-token";
}

/// <summary>
/// Authenticates <c>solomcp_</c> tokens against the extension's own table.
/// </summary>
/// <remarks>
/// <para>The principal carries the raw OIDC issuer and subject claims a provider
/// token would, because that pair is what core keys users on — and nothing
/// else, see <see cref="HandleAuthenticateAsync"/>. Core's own
/// <c>UserProvisioningService</c> then resolves the row,
/// so project authorization, auditing and the extension query API all behave as
/// though the user had signed in, with no core code aware that MCP exists.</para>
///
/// <para>Unlike a session, an MCP token has no expiry to slide: it works until
/// its owner regenerates it or an admin revokes it. What it does instead is
/// count itself, one row per token per hour, which is the series behind the
/// admin's 24-hour usage graph. Every accepted request is counted — including
/// the REST calls the MCP endpoint makes on the caller's behalf, so the graph
/// measures requests rather than tool invocations.</para>
///
/// <para>Neither the lookup nor the count touches the database per request:
/// this runs ahead of core's rate limiter, so anything it costs, a throttled
/// request has already paid. Lookups come from <see cref="McpTokenCache"/> and
/// counts go to <see cref="McpUsageCounter"/>.</para>
/// </remarks>
public sealed class McpTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = McpTokens.FromRequest(Request);
        if (!McpTokens.IsMcpToken(token))
        {
            return AuthenticateResult.NoResult();
        }

        var cache = Context.RequestServices.GetRequiredService<McpTokenCache>();
        var hash = McpTokens.Hash(token!);
        if (!cache.TryGet(hash, out var record))
        {
            // Read before the query, so a revocation landing while it runs
            // leaves what it found stale rather than cached as live.
            var generation = McpTokenCache.Generation;
            var db = Context.RequestServices.GetRequiredService<McpDbContext>();
            record = await db.Tokens.AsNoTracking()
                .Where(t => t.TokenHash == hash)
                .Select(t => new McpTokenCache.Lookup(t.Id, t.Issuer, t.Subject, t.RevokedAt, t.RevokedReason))
                .FirstOrDefaultAsync(Context.RequestAborted);
            cache.Set(hash, record, generation);
        }

        if (record is null)
        {
            return AuthenticateResult.Fail("Unknown MCP token.");
        }

        if (record.RevokedAt is not null)
        {
            // Named, because the whole point of regeneration is that whoever
            // holds the old token finds out why it stopped working.
            return AuthenticateResult.Fail(
                $"This MCP token was revoked on {record.RevokedAt:u} ({record.RevokedReason}). " +
                "Generate a new one from MCP access in Solo.");
        }

        Context.RequestServices.GetRequiredService<McpUsageCounter>().Record(record.Id);

        // Identity only. Provisioning treats an email claim as the provider's
        // fresh word for it — it activates invites to that address and
        // overwrites the user's own — but this is a snapshot taken when the
        // token was minted, with no email_verified beside it. Issuer and
        // subject are enough to find the user, and the row holds the profile.
        Claim[] claims =
        [
            new("iss", record.Issuer),
            new("sub", record.Subject),
            new(McpAuthentication.TokenIdClaim, record.Id.ToString()),
        ];

        var identity = new ClaimsIdentity(claims, Scheme.Name, nameType: "name", roleType: "role");
        return AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
}

/// <summary>
/// Puts this extension's authentication at the front of the pipeline, so a
/// principal exists before core decides who the caller is.
/// </summary>
/// <remarks>
/// <para><b>Why a startup filter.</b> An extension gets <c>ConfigureServices</c>
/// and an application part — it cannot call <c>app.Use…</c>, and it must not
/// reach into core's OIDC options to bolt itself onto their forward selector.
/// <see cref="IStartupFilter"/> is the supported way to add middleware from the
/// service graph alone, and it is registered exactly like any other service.</para>
///
/// <para><b>Why first, rather than at the MCP endpoint.</b> Core's
/// <c>CurrentUserMiddleware</c> provisions the user from <c>HttpContext.User</c>
/// before authorization runs, and everything downstream — project policies, the
/// audit interceptor's actor, the extension query API — reads what it produced.
/// Authenticating inside the endpoint would be too late for all of it, and the
/// REST calls the MCP tools make would arrive as anonymous.</para>
///
/// <para>Core's own authentication runs later and leaves this principal alone: it
/// overwrites <c>HttpContext.User</c> only when its default scheme succeeds, and
/// an MCP token is not a JWT and does not carry core's session prefix, so it
/// never does.</para>
/// </remarks>
public sealed class McpAuthenticationStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
        builder =>
        {
            builder.UseMiddleware<McpTokenAuthenticationMiddleware>();
            next(builder);
        };
}

/// <summary>Assigns the MCP principal, for requests that carry an MCP token and no other.</summary>
public sealed class McpTokenAuthenticationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        // A cheap prefix test, because this sits in front of every request the
        // instance serves — including each framework file of a cold client load.
        if (McpTokens.IsMcpToken(McpTokens.FromRequest(context.Request)))
        {
            var result = await context.AuthenticateAsync(McpAuthentication.Scheme);
            if (result.Succeeded)
            {
                context.User = result.Principal!;
            }

            // A bad or revoked token deliberately falls through as anonymous:
            // the endpoint's own [Authorize] answers 401, and the reason reaches
            // the log rather than an attacker.
        }

        await next(context);
    }
}
