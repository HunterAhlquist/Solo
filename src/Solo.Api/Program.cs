using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Solo.Api;
using Solo.Api.Auth;
using Solo.Api.Controllers;
using Solo.Api.Realtime;
using Solo.Api.Services;
using Solo.Api.Theming;
using Solo.Data;
using Solo.Data.Auditing;
using Solo.Data.Domain;
using Solo.Data.Entities;
using Solo.Extensibility;
using Solo.Extensions;
using Solo.Shared.Hubs;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Extensions. First, because MVC application parts and filters cannot be added
// after Build(): an extension's controllers and hooks have to be registered
// before the container is built. Nothing here throws — a package that fails to
// load is recorded and skipped, and the instance starts without it, the same
// way a bad theme package is reported rather than fatal.
// ---------------------------------------------------------------------------
var extensions = builder.AddSoloExtensions();

// ---------------------------------------------------------------------------
// Persistence
// ---------------------------------------------------------------------------
builder.Services.AddScoped<CurrentUserAccessor>();
builder.Services.AddScoped<ICurrentUserProvider>(sp => sp.GetRequiredService<CurrentUserAccessor>());
builder.Services.AddScoped<AuditSaveChangesInterceptor>();
builder.Services.AddDbContext<SoloDbContext>((sp, options) =>
{
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default"));
    options.AddInterceptors(sp.GetRequiredService<AuditSaveChangesInterceptor>());
});

// ---------------------------------------------------------------------------
// Authentication. The default "Bearer" scheme validates OIDC ID tokens, but
// forwards solo_ tokens to the session scheme — long-lived sessions minted by
// POST api/auth/sessions for clients that can't renew provider tokens
// silently (the desktop app). Plus the setup-only static-credential scheme
// used until first-run handoff.
// ---------------------------------------------------------------------------
var authority = builder.Configuration["Auth:Authority"];
var clientId = builder.Configuration["Auth:ClientId"];

var authBuilder = builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = AuthConstants.BearerScheme;
});
authBuilder.AddScheme<AuthenticationSchemeOptions, SetupAuthenticationHandler>(
    AuthConstants.SetupScheme, null);
authBuilder.AddScheme<AuthenticationSchemeOptions, SessionAuthenticationHandler>(
    AuthConstants.SessionScheme, null);
if (!string.IsNullOrEmpty(authority))
{
    authBuilder.AddJwtBearer(AuthConstants.BearerScheme, options =>
    {
        options.Authority = authority;
        options.MapInboundClaims = false;
        options.TokenValidationParameters.ValidAudience = clientId;
        options.TokenValidationParameters.NameClaimType = "name";
        // solo_ tokens are Solo sessions, not JWTs — hand them to that scheme.
        options.ForwardDefaultSelector = context =>
            SessionTokens.IsSessionToken(SessionTokens.FromRequest(context.Request))
                ? AuthConstants.SessionScheme
                : null;
        // SignalR websocket connections carry the token as a query parameter.
        options.Events = new Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(accessToken) &&
                    context.HttpContext.Request.Path.StartsWithSegments(BoardHubConstants.Path))
                {
                    context.Token = accessToken;
                }

                return Task.CompletedTask;
            },
        };
    });
}

builder.Services.AddScoped<UserProvisioningService>();
// Desktop shell browser sign-in: pending PKCE loopback flows (see DesktopAuthController).
builder.Services.AddSingleton<DesktopSignInFlows>();
builder.Services.AddScoped<SoloDbContextRoleLookup>();
builder.Services.AddScoped<IExtensionCallerAccessor, ExtensionCallerAccessor>();
builder.Services.AddScoped<IAuthorizationHandler, ProjectAccessHandler>();
builder.Services.AddScoped<IAuthorizationHandler, SuperAdminHandler>();
builder.Services.AddAuthorizationBuilder()
    // Endpoints carrying no authorization metadata of their own require a
    // signed-in caller, so an action added without an [Authorize] fails closed
    // instead of shipping as an anonymous hole. The handful of genuinely public
    // endpoints say so with [AllowAnonymous], which still wins over this.
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
    .AddPolicy(AuthConstants.SetupPolicy, p => p
        .AddAuthenticationSchemes(AuthConstants.SetupScheme)
        .RequireRole(AuthConstants.SetupAdminRole))
    .AddPolicy(AuthConstants.SuperAdminPolicy, p => p
        .RequireAuthenticatedUser()
        .AddRequirements(new SuperAdminRequirement()))
    .AddPolicy(AuthConstants.ProjectReadPolicy, p => p
        .RequireAuthenticatedUser()
        .AddRequirements(new ProjectAccessRequirement(ProjectAccessLevel.Read)))
    .AddPolicy(AuthConstants.ProjectWritePolicy, p => p
        .RequireAuthenticatedUser()
        .AddRequirements(new ProjectAccessRequirement(ProjectAccessLevel.Write)))
    .AddPolicy(AuthConstants.ProjectOwnerPolicy, p => p
        .RequireAuthenticatedUser()
        .AddRequirements(new ProjectAccessRequirement(ProjectAccessLevel.Owner)));

// ---------------------------------------------------------------------------
// Web stack
// ---------------------------------------------------------------------------
builder.Services.AddControllers()
    .AddExtensionControllers(extensions)
    .AddJsonOptions(options =>
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddSignalR();
builder.Services.AddHttpContextAccessor();
builder.Services.AddMemoryCache();

// Themes: built-ins from the image plus whatever is in the themes volume, read
// once into memory at startup (see ThemeCatalog) and re-read on admin request.
builder.Services.Configure<ThemeOptions>(builder.Configuration.GetSection(ThemeOptions.Section));
builder.Services.AddSingleton<ThemeCatalog>();
// Server-side avatar fetch: SSO picture CDNs (Google) rate-limit direct browser
// hotlinking at board-render volume (429), so the API fetches each avatar once and
// caches it, and the browser only ever loads from our own origin.
builder.Services.AddHttpClient(AvatarConstants.HttpClient, client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Solo-Avatar-Proxy/1.0");
})
    // This is the only place the server fetches a URL it did not choose — the
    // picture claim out of an OIDC token. On a self-hosted instance the API can
    // reach the database, the container network and the LAN, so the guard sits
    // on the socket rather than on the URL: it holds for redirects (which
    // HttpClient follows on its own) and leaves no window between resolving a
    // name and connecting to it.
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
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
    });
builder.Services.AddSingleton<PresenceRegistry>();
builder.Services.AddScoped<IBoardNotifier, BoardNotifier>();
builder.Services.AddHostedService<AuditRetentionService>();
builder.Services.AddHealthChecks().AddDbContextCheck<SoloDbContext>();

// ---------------------------------------------------------------------------
// Rate limiting. Production sits behind Nginx Proxy Manager, so per-IP
// partitions need X-Forwarded-For applied first. Limits are config-overridable
// mainly so tests can tighten them.
//
// X-Forwarded-For is only honoured from a peer we expect to be a reverse proxy:
// loopback, or a private/link-local address (the container network the proxy
// and the app share). A caller that reaches the app from a public address gets
// its header ignored and is metered on the connection address instead —
// otherwise anyone on the internet could rotate the header and hand themselves
// an unlimited number of rate-limit partitions, which is the difference between
// "10 setup-password guesses a minute" and "as many as you like". Deployments
// whose proxy sits on a public address can name it in
// ForwardedHeaders:KnownProxies (comma-separated).
// ---------------------------------------------------------------------------
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
    foreach (var network in TrustedProxies.Networks)
    {
        options.KnownIPNetworks.Add(network);
    }

    foreach (var proxy in TrustedProxies.Configured(builder.Configuration))
    {
        options.KnownProxies.Add(proxy);
    }

    // Chains longer than one hop (proxy in front of a tunnel) need this raised
    // to match, or the near end of the chain is all that's read.
    options.ForwardLimit = builder.Configuration.GetValue("ForwardedHeaders:ForwardLimit", 1);
});
builder.Services.AddRateLimiter(limiter =>
{
    limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    limiter.OnRejected = (context, _) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter =
                Math.Ceiling(retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        }

        return ValueTask.CompletedTask;
    };

    // Everything under /api shares one generous bucket per caller — enough for
    // a board render's burst of avatar/image loads, but a runaway client gets
    // cut off. The rest of the host is exempt: the WASM client pulls dozens of
    // framework files per cold load, the SignalR hub holds one long-lived
    // connection, and /healthz answers infrastructure probes.
    var apiBurst = builder.Configuration.GetValue("RateLimiting:Api:Burst", 300);
    var apiTokensPerPeriod = builder.Configuration.GetValue("RateLimiting:Api:TokensPerPeriod", 100);
    var apiPeriod = TimeSpan.FromSeconds(builder.Configuration.GetValue("RateLimiting:Api:PeriodSeconds", 10));
    limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        context.Request.Path.StartsWithSegments("/api")
            ? RateLimitPartition.GetTokenBucketLimiter(RateLimitPartitions.Client(context),
                _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = apiBurst,
                    TokensPerPeriod = apiTokensPerPeriod,
                    ReplenishmentPeriod = apiPeriod,
                    QueueLimit = 0,
                })
            : RateLimitPartition.GetNoLimiter("exempt"));

    var authPermits = builder.Configuration.GetValue("RateLimiting:Auth:PermitLimit", 10);
    var authWindow = TimeSpan.FromSeconds(builder.Configuration.GetValue("RateLimiting:Auth:WindowSeconds", 60));
    limiter.AddPolicy(RateLimitPolicies.Auth, context =>
        RateLimitPartition.GetFixedWindowLimiter(RateLimitPartitions.Ip(context),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = authPermits,
                Window = authWindow,
                QueueLimit = 0,
            }));

});

var app = builder.Build();

app.UseForwardedHeaders();
app.UseSoloSecurityHeaders();
app.UseExceptionHandler();
app.UseStatusCodePages();

// Code-first migrations applied automatically on startup — acceptable for the
// single-instance, small-team target. Also guarantees the settings row exists.
if (app.Configuration.GetValue("Database:AutoMigrate", true))
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<SoloDbContext>();
    await db.Database.MigrateAsync();
    if (!await db.InstanceSettings.AnyAsync(s => s.Id == InstanceSettings.SingletonId))
    {
        db.InstanceSettings.Add(new InstanceSettings());
        await db.SaveChangesAsync();
    }
}

// Extension schemas, after core's and gated on the same AutoMigrate flag. A
// failure here disables only its own extension: its routes and hooks were
// registered before Build(), so without this it would serve requests against
// tables that do not exist. Also reconciles the registry, which is what lets an
// extension whose package has been deleted still be named — and its surviving
// data offered for purge — rather than becoming an orphan schema.
await app.MigrateExtensionsAsync(extensions);

// Build the theme catalog now rather than on whichever request first asks for a
// theme. It reads the filesystem, so anything wrong with the themes volume
// belongs in the startup log next to the migration, where an operator looks —
// not in the middle of an unrelated request. Rescan() handles its own failures.
app.Services.GetRequiredService<ThemeCatalog>();

// Serves the Blazor WASM client: framework files (_framework/*) first, then the
// client's own wwwroot assets, with index.html as the SPA fallback below.
app.UseBlazorFrameworkFiles();
app.UseStaticFiles(new StaticFileOptions
{
    // index.html, css/app.css and js/solo-interop.js carry no fingerprint, and
    // ASP.NET sends them with an ETag but no Cache-Control — which lets a browser
    // serve a stale copy without revalidating. "no-cache" still allows caching;
    // it just requires an If-None-Match round trip, which the ETag answers with a
    // 304. (_framework/* is fingerprinted and handled by UseBlazorFrameworkFiles.)
    OnPrepareResponse = context =>
        context.Context.Response.Headers.CacheControl = "no-cache",
});

app.UseAuthentication();
// After authentication so authenticated callers are metered per user, before
// CurrentUserMiddleware so a throttled request never costs a database lookup.
app.UseRateLimiter();
app.UseMiddleware<CurrentUserMiddleware>();
app.UseAuthorization();

app.MapControllers();
app.MapHub<BoardHub>(BoardHubConstants.Path);
app.MapHealthChecks("/healthz").AllowAnonymous();
// An unknown route under the API namespace is a 404, not the SPA shell: a
// client calling a route that doesn't exist should get JSON it can read, not a
// 200 of index.html that masks the mistake. This is more specific than the SPA
// fallback below, so it wins for /api paths; the rest still boot the client.
// Anonymous so a genuinely-missing route answers 404 rather than the fallback
// policy's 401 — real endpoints match first and keep enforcing their own auth.
app.MapFallback("/api/{**_}", () => Results.Problem(statusCode: StatusCodes.Status404NotFound))
    .AllowAnonymous();
// The SPA shell itself is public — the client boots, then authenticates.
app.MapFallbackToFile("index.html").AllowAnonymous();

app.Run();

/// <summary>Exposed for WebApplicationFactory-based integration tests.</summary>
public partial class Program;
