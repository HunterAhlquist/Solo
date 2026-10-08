using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Solo.Extensibility;

namespace Solo.Extensions;

/// <summary>Wires the extension system into the host, on both sides of <c>builder.Build()</c>.</summary>
public static class ExtensionStartup
{
    /// <summary>
    /// Discovers and activates extensions. Must run before <c>Build()</c>:
    /// application parts and MVC filters cannot be added afterwards, which is
    /// why extension loading is the first thing in Program.cs.
    /// </summary>
    public static ExtensionState AddSoloExtensions(this WebApplicationBuilder builder)
    {
        var options = new ExtensionOptions();
        builder.Configuration.GetSection(ExtensionOptions.Section).Bind(options);
        builder.Services.Configure<ExtensionOptions>(
            builder.Configuration.GetSection(ExtensionOptions.Section));

        // A logger factory before the host exists, so package failures land in
        // the startup log next to the migration rather than nowhere.
        using var loggerFactory = LoggerFactory.Create(logging => logging.AddConsole());
        var connectionString = builder.Configuration.GetConnectionString("Default") ?? string.Empty;

        var state = ExtensionLoader.Load(builder.Services, options, connectionString, loggerFactory);

        builder.Services.AddSingleton(state);
        builder.Services.AddScoped<ExtensionAuditContributions>();
        builder.Services.AddScoped<ExtensionCredentialIssuers>();
        builder.Services.AddScoped<ExtensionAuditInterceptor>();
        builder.Services.AddSingleton<HookCatalog>();
        // Scoped, not singleton: handlers are scoped (they hold the extension's
        // own DbContext), so the publisher has to resolve them from the request
        // scope. As a singleton it would resolve from the root provider, which
        // the container refuses outright.
        builder.Services.AddScoped<IDomainEventPublisher, DomainEventPublisher>();
        builder.Services.AddScoped<ExtensionRegistry>();
        builder.Services.AddScoped<ISoloQuery, SoloQuery>();
        builder.Services.AddScoped<ExtensionCallerOverride>();
        builder.Services.AddSingleton<BackgroundTokens>();
        builder.Services.AddHttpClient(ExtensionBackground.HttpClientName);

        // Each extension gets a field store bound to its own id, so "you may
        // only touch your own key" is structural rather than a convention.
        foreach (var package in state.Packages)
        {
            var id = package.Id;
            builder.Services.AddKeyedScoped<IExtensionFieldStore>(id, (sp, _) =>
                new ExtensionFieldStore(sp.GetRequiredService<Solo.Data.SoloDbContext>(), id,
                    sp.GetService<IExtensionFieldNotifier>()));

            // The same for background work: the id it may act under is the one
            // it was registered with, not one it passes in. A singleton, so an
            // extension's hosted service can take it in its constructor.
            builder.Services.AddKeyedSingleton<ISoloBackground>(id, (sp, _) =>
                ActivatorUtilities.CreateInstance<ExtensionBackground>(sp, id));
        }

        var assemblyMap = state.Packages
            .Where(p => p.LoadedAssembly is not null)
            .ToDictionary(
                p => p.LoadedAssembly!.GetName().Name!,
                p => p.Id,
                StringComparer.Ordinal);

        builder.Services.Configure<MvcOptions>(mvc =>
        {
            mvc.Conventions.Add(new ExtensionRouteConvention(assemblyMap));
            mvc.Filters.Add<ExtensionGateFilter>();
            mvc.Filters.Add<ExtensionHookFilter>();
        });

        return state;
    }

    /// <summary>
    /// Registers each extension's controllers. Separate from
    /// <see cref="AddSoloExtensions"/> only because it needs the
    /// <see cref="IMvcBuilder"/> that <c>AddControllers()</c> returns.
    /// </summary>
    public static IMvcBuilder AddExtensionControllers(this IMvcBuilder mvc, ExtensionState state)
    {
        foreach (var package in state.Packages.Where(p => p.LoadedAssembly is not null))
        {
            mvc.AddApplicationPart(package.LoadedAssembly!);
        }

        return mvc;
    }

    /// <summary>
    /// Migrates each extension's own context, then reconciles the registry.
    /// </summary>
    /// <remarks>
    /// <para>Runs after core's migration, gated on the same
    /// <c>Database:AutoMigrate</c> flag: an operator who runs core migrations
    /// manually in a controlled deploy expects extension migrations to be manual
    /// too.</para>
    ///
    /// <para>A failure disables only its own extension. This is the other half
    /// of the ordering trap — the extension's routes and hooks were registered
    /// before Build(), so without disabling it here it would serve requests
    /// against tables that do not exist.</para>
    ///
    /// <para>Runs even with no packages installed: reconcile is what marks a
    /// removed package <c>Absent</c>, so skipping it when the last package goes
    /// would leave that registration claiming <c>Loaded</c> indefinitely.</para>
    /// </remarks>
    public static async Task MigrateExtensionsAsync(this WebApplication app, ExtensionState state)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ExtensionStartup));
        await using var scope = app.Services.CreateAsyncScope();
        var registry = scope.ServiceProvider.GetRequiredService<ExtensionRegistry>();

        if (app.Configuration.GetValue("Database:AutoMigrate", true))
        {
            var registrations = app.Services.GetServices<ExtensionDbContextRegistration>().ToList();
            foreach (var registration in registrations)
            {
                var package = state.Find(registration.ExtensionId);
                if (package is null || state.IsDisabled(registration.ExtensionId, out _))
                {
                    continue;
                }

                var downgrade = await registry.CheckDowngradeAsync(package);
                if (downgrade is not null)
                {
                    logger.LogError("Extension '{Id}' refused: {Reason}", package.Id, downgrade);
                    state.Disable(package.Id, downgrade);
                    continue;
                }

                try
                {
                    // Bracketing the noise: EF probes the history table before it
                    // exists, so a first install logs a failed SELECT from the
                    // extension's __EFMigrationsHistory — at Error level, right
                    // between these two lines. That is the probe finding the table
                    // absent, not a failure; core's own first migration does the
                    // same thing against public. This line and the one after it
                    // are what actually report the outcome.
                    logger.LogInformation(
                        "Extension '{Id}': applying migrations to schema {Schema}.",
                        registration.ExtensionId, registration.Schema);

                    var context = (DbContext)scope.ServiceProvider.GetRequiredService(registration.ContextType);
                    await context.Database.MigrateAsync();
                    logger.LogInformation(
                        "Extension '{Id}' migrated into schema {Schema}.",
                        registration.ExtensionId, registration.Schema);
                }
                catch (Exception e)
                {
                    logger.LogError(
                        e, "Extension '{Id}' migration failed; the extension is disabled.",
                        registration.ExtensionId);
                    state.Disable(registration.ExtensionId,
                        $"Its database migration failed: {e.Message}");
                }
            }
        }

        await registry.ReconcileAsync(state);
    }
}
