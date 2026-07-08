using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Solo.Api.Auth;
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
// Authentication: OIDC bearer tokens from a configurable provider, plus the
// setup-only static-credential scheme used until first-run handoff.
// ---------------------------------------------------------------------------
var authority = builder.Configuration["Auth:Authority"];
var clientId = builder.Configuration["Auth:ClientId"];

var authBuilder = builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = AuthConstants.BearerScheme;
});
authBuilder.AddScheme<AuthenticationSchemeOptions, SetupAuthenticationHandler>(
    AuthConstants.SetupScheme, null);
if (!string.IsNullOrEmpty(authority))
{
    authBuilder.AddJwtBearer(AuthConstants.BearerScheme, options =>
    {
        options.Authority = authority;
        options.MapInboundClaims = false;
        options.TokenValidationParameters.ValidAudience = clientId;
        options.TokenValidationParameters.NameClaimType = "name";
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
builder.Services.AddSingleton<PresenceRegistry>();
builder.Services.AddScoped<IBoardNotifier, BoardNotifier>();
builder.Services.AddHostedService<AuditRetentionService>();
builder.Services.AddHealthChecks().AddDbContextCheck<SoloDbContext>();

var app = builder.Build();

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

// Serves the published Blazor WASM client when present.
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthentication();
app.UseMiddleware<CurrentUserMiddleware>();
app.UseAuthorization();

app.MapControllers();
app.MapHub<BoardHub>(BoardHubConstants.Path);
app.MapHealthChecks("/healthz").AllowAnonymous();
app.MapFallbackToFile("index.html");

app.Run();

/// <summary>Exposed for WebApplicationFactory-based integration tests.</summary>
public partial class Program;
