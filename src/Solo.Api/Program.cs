using System.Globalization;
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
using Solo.Data;
using Solo.Data.Auditing;
using Solo.Data.Domain;
using Solo.Data.Entities;
using Solo.Shared.Hubs;

var builder = WebApplication.CreateBuilder(args);

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
// Desktop shell browser sign-in: pending device-code flows (see DesktopAuthController).
builder.Services.AddSingleton<DesktopSignInFlows>();
builder.Services.AddScoped<IAuthorizationHandler, ProjectAccessHandler>();
builder.Services.AddScoped<IAuthorizationHandler, SuperAdminHandler>();
builder.Services.AddAuthorizationBuilder()
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
builder.Services.AddControllers().AddJsonOptions(options =>
    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddSignalR();
builder.Services.AddHttpContextAccessor();
builder.Services.AddMemoryCache();
// Server-side avatar fetch: SSO picture CDNs (Google) rate-limit direct browser
// hotlinking at board-render volume (429), so the API fetches each avatar once and
// caches it, and the browser only ever loads from our own origin.
builder.Services.AddHttpClient(AvatarConstants.HttpClient, client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Solo-Avatar-Proxy/1.0");
});
builder.Services.AddSingleton<PresenceRegistry>();
builder.Services.AddScoped<IBoardNotifier, BoardNotifier>();
builder.Services.AddHostedService<AuditRetentionService>();
builder.Services.AddHealthChecks().AddDbContextCheck<SoloDbContext>();

// ---------------------------------------------------------------------------
// Rate limiting. Production sits behind Nginx Proxy Manager, so per-IP
// partitions need X-Forwarded-For applied first. Trusting the header from any
// direct connection is acceptable here: the API is only reachable through the
// proxy (or the LAN), and the worst a spoofed header buys is a fresh limit
// partition. Limits are config-overridable mainly so tests can tighten them.
// ---------------------------------------------------------------------------
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
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

    // 2s polling is 30/min per pending sign-in; allow a few flows per address.
    var pollPermits = builder.Configuration.GetValue("RateLimiting:AuthPoll:PermitLimit", 120);
    var pollWindow = TimeSpan.FromSeconds(builder.Configuration.GetValue("RateLimiting:AuthPoll:WindowSeconds", 60));
    limiter.AddPolicy(RateLimitPolicies.AuthPoll, context =>
        RateLimitPartition.GetFixedWindowLimiter(RateLimitPartitions.Ip(context),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = pollPermits,
                Window = pollWindow,
                QueueLimit = 0,
            }));
});

var app = builder.Build();

app.UseForwardedHeaders();
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
app.MapFallbackToFile("index.html");

app.Run();

/// <summary>Exposed for WebApplicationFactory-based integration tests.</summary>
public partial class Program;
