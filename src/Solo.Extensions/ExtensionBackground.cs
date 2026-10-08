using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Solo.Data;
using Solo.Data.Entities;
using Solo.Extensibility;

namespace Solo.Extensions;

/// <summary>
/// One extension's <see cref="ISoloBackground"/>, bound to its id the way its
/// field store is, so "act only as users who granted <em>you</em>" is
/// structural rather than an argument the extension could get wrong.
/// </summary>
/// <remarks>
/// <para>Writes go over HTTP back into the API, as MCP's do and for the same
/// reason: every core write path is wrapped in authorization, other
/// extensions' hooks, the audit interceptor and the live broadcast, and
/// re-entering the pipeline gets all of it by construction. A private write
/// path for background work would be the hole nobody noticed.</para>
///
/// <para>The credential on each of those requests is minted per request by
/// <see cref="BackgroundTokens"/> and names the grant, which the
/// authentication handler re-reads every time. So a grant revoked mid-run ends
/// the run's next write, not the next run.</para>
/// </remarks>
public sealed class ExtensionBackground(
    string extensionId,
    IServiceScopeFactory scopes,
    IHttpContextAccessor http,
    ExtensionState state,
    BackgroundTokens tokens,
    IHttpMessageHandlerFactory handlers,
    IOptions<ExtensionOptions> options,
    IServer server)
    : ISoloBackground
{
    /// <summary>The named client behind <see cref="SoloBackgroundScope.Api"/>; tests point its handler at the test server.</summary>
    public const string HttpClientName = "solo-extension-background";

    public async Task GrantAsync(CancellationToken ct = default)
    {
        var context = http.HttpContext ?? throw new InvalidOperationException(
            "A background grant has to be made in a request from the person granting it.");

        // Work running as someone can't extend its own reach: a grant is the
        // person's decision, made while they are actually here.
        if (context.User.Identity?.AuthenticationType == BackgroundTokens.Scheme)
        {
            throw new InvalidOperationException("Background work can't grant itself; a grant has to come from the person.");
        }

        RequireEnabled();
        var (caller, _) = await context.RequestServices.GetRequiredService<IExtensionCallerAccessor>()
            .CurrentAsync(context, ct);
        if (caller.UserId == Guid.Empty)
        {
            throw new InvalidOperationException("Only a signed-in Solo user can grant background access.");
        }

        // The request's own context, so the grant is audited as theirs.
        var db = context.RequestServices.GetRequiredService<SoloDbContext>();
        if (await HasGrantAsync(db, caller.UserId, ct))
        {
            return;
        }

        var grant = db.ExtensionGrants.Add(new ExtensionGrant { ExtensionId = extensionId, UserId = caller.UserId });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            grant.State = EntityState.Detached;
            if (!await HasGrantAsync(db, caller.UserId, ct))
            {
                throw;
            }

            // Two tabs granting at once: the other one won, which is the same outcome.
        }
    }

    public async Task<bool> RevokeAsync(Guid userId, CancellationToken ct = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SoloDbContext>();
        return await db.ExtensionGrants
            .Where(g => g.ExtensionId == extensionId && g.UserId == userId)
            .ExecuteDeleteAsync(ct) > 0;
    }

    public async Task<bool> IsGrantedAsync(Guid userId, CancellationToken ct = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await HasGrantAsync(scope.ServiceProvider.GetRequiredService<SoloDbContext>(), userId, ct);
    }

    public async Task<bool> RunAsAsync(
        Guid userId, Func<SoloBackgroundScope, CancellationToken, Task> work, CancellationToken ct = default)
    {
        if (state.IsDisabled(extensionId, out _))
        {
            return false;
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SoloDbContext>();
        var grant = await db.ExtensionGrants.AsNoTracking()
            .Include(g => g.User)
            .FirstOrDefaultAsync(g => g.ExtensionId == extensionId && g.UserId == userId, ct);
        if (grant is null)
        {
            return false;
        }

        // Never super admin, here or on the API (see the authentication handler):
        // background access is for project work, not for administering the instance.
        var caller = new ExtensionCaller(
            grant.User.Id, grant.User.Email, grant.User.DisplayName, IsSuperAdmin: false, Role: null,
            IsProjectOwner: false);
        scope.ServiceProvider.GetRequiredService<ExtensionCallerOverride>().Caller = caller;

        // The factory's handler is pooled and owned by the factory, so neither the
        // client nor the token handler wrapped around it disposes it.
        using var api = new HttpClient(
            new GrantTokenHandler(tokens, grant.Id) { InnerHandler = handlers.CreateHandler(HttpClientName) },
            disposeHandler: false)
        {
            BaseAddress = BaseAddress(),
        };

        await work(new SoloBackgroundScope
        {
            Caller = caller,
            Query = scope.ServiceProvider.GetRequiredService<ISoloQuery>(),
            Api = api,
            Services = scope.ServiceProvider,
        }, ct);
        return true;
    }

    private Task<bool> HasGrantAsync(SoloDbContext db, Guid userId, CancellationToken ct) =>
        db.ExtensionGrants.AsNoTracking().AnyAsync(g => g.ExtensionId == extensionId && g.UserId == userId, ct);

    private void RequireEnabled()
    {
        if (state.IsDisabled(extensionId, out var reason))
        {
            throw new InvalidOperationException($"Extension '{extensionId}' is disabled: {reason}");
        }
    }

    /// <summary>
    /// The configured address, else the one the server listens on with its
    /// wildcard host rewritten to loopback — which keeps the hop inside the
    /// container rather than out through the reverse proxy and back. There is no
    /// request to fall back on, so with neither it refuses rather than guess.
    /// </summary>
    private Uri BaseAddress()
    {
        if (!string.IsNullOrWhiteSpace(options.Value.ApiBaseUrl))
        {
            return new Uri(WithTrailingSlash(options.Value.ApiBaseUrl), UriKind.Absolute);
        }

        // Prefer http: a loopback hop to an https listener would have to trust a
        // certificate issued for a name we are deliberately not using.
        var listen = server.Features.Get<IServerAddressesFeature>()?.Addresses
            .OrderBy(a => a.StartsWith("https", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
        if (listen is not null && Uri.TryCreate(Loopback(listen), UriKind.Absolute, out var address))
        {
            return address;
        }

        throw new InvalidOperationException(
            $"Background work can't tell where this instance listens. Set {ExtensionOptions.Section}__ApiBaseUrl.");
    }

    private static string Loopback(string address) => WithTrailingSlash(
        address.Replace("://[::]", "://127.0.0.1", StringComparison.Ordinal)
            .Replace("://0.0.0.0", "://127.0.0.1", StringComparison.Ordinal)
            .Replace("://+", "://127.0.0.1", StringComparison.Ordinal)
            .Replace("://*", "://127.0.0.1", StringComparison.Ordinal));

    private static string WithTrailingSlash(string url) => url.EndsWith('/') ? url : url + "/";

    /// <summary>A fresh credential on every request, so long-running work never holds an expired one.</summary>
    private sealed class GrantTokenHandler(BackgroundTokens tokens, Guid grantId) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            request.Headers.Authorization = new("Bearer", tokens.Mint(grantId));
            return base.SendAsync(request, ct);
        }
    }
}
