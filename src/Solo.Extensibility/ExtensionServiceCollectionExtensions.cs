using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Solo.Extensibility;

/// <summary>Registration helpers an extension uses from <see cref="ISoloExtension.ConfigureServices"/>.</summary>
public static class ExtensionServiceCollectionExtensions
{
    /// <summary>
    /// Registers the extension's own <see cref="DbContext"/> and records it for
    /// startup migration.
    /// </summary>
    /// <remarks>
    /// <para>Not plain <c>AddDbContext</c>: the host supplies the connection
    /// string and pins the migrations history table into the extension's schema,
    /// so two extensions can never share or contend a history table, and core's
    /// stays untouched.</para>
    ///
    /// <para>The context must declare <c>HasDefaultSchema</c> matching the
    /// manifest's schema. Migrations are scaffolded by the extension author at
    /// build time exactly as core's are — they cannot be generated at runtime —
    /// so the project needs its own <c>IDesignTimeDbContextFactory</c>.</para>
    /// </remarks>
    public static IServiceCollection AddExtensionDbContext<TContext>(
        this IServiceCollection services, IExtensionContext context)
        where TContext : DbContext
    {
        if (string.IsNullOrWhiteSpace(context.Schema))
        {
            throw new InvalidOperationException(
                $"Extension '{context.Id}' registered a DbContext but its manifest declares no \"schema\".");
        }

        services.AddDbContext<TContext>(options => options.UseNpgsql(
            context.ConnectionString,
            npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", context.Schema)));

        // The host reads these back after Build() to migrate each one.
        services.AddSingleton(new ExtensionDbContextRegistration(context.Id, context.Schema, typeof(TContext)));
        return services;
    }

    /// <summary>Registers a hook. Operations outside the manifest's declared list are dropped at dispatch.</summary>
    public static IServiceCollection AddOperationHook<THook>(this IServiceCollection services)
        where THook : class, ISoloOperationHook
    {
        services.AddScoped<ISoloOperationHook, THook>();
        return services;
    }

    /// <summary>Registers the extension's single client-message handler (contract 1.10).</summary>
    public static IServiceCollection AddLiveMessageHandler<THandler>(this IServiceCollection services)
        where THandler : class, ISoloLiveMessageHandler
    {
        if (services.Any(s => s.ImplementationInstance is LiveMessageHandlerRegistration r &&
                              r.HandlerType.Assembly == typeof(THandler).Assembly))
            throw new InvalidOperationException("An extension may register only one live message handler.");
        services.AddScoped<THandler>();
        services.AddSingleton(new LiveMessageHandlerRegistration(typeof(THandler)));
        return services;
    }

    /// <summary>Registers a domain event handler.</summary>
    public static IServiceCollection AddEventHandler<THandler>(this IServiceCollection services)
        where THandler : class, ISoloEventHandler
    {
        services.AddScoped<ISoloEventHandler, THandler>();
        return services;
    }

    /// <summary>
    /// Registers the revoker for credentials this extension issues, so signing a
    /// user out everywhere reaches them too (contract 1.7).
    /// </summary>
    public static IServiceCollection AddCredentialIssuer<TIssuer>(this IServiceCollection services)
        where TIssuer : class, ISoloCredentialIssuer
    {
        services.AddScoped<ISoloCredentialIssuer, TIssuer>();
        return services;
    }
}

/// <summary>Records an extension context type so the host can migrate it after Build().</summary>
public sealed record ExtensionDbContextRegistration(string ExtensionId, string Schema, Type ContextType);
