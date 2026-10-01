using Microsoft.Extensions.DependencyInjection;
using Solo.Extensibility;
using Solo.Roadmap.Data;

namespace Solo.Roadmap;

/// <summary>
/// The package's entry point — the one type the host looks for.
/// </summary>
/// <remarks>
/// Roadmaps, their entries and the timeline settings live in this package's own
/// schema; core is read through <see cref="ISoloQuery"/> and never written from
/// here. The one thing the feature needs from core beyond the contract's reads —
/// a task's start date and its schedule endpoint — the client half calls like
/// any other client would, so authorization, hooks, audit and the live board
/// broadcast all apply to it unchanged.
/// </remarks>
public sealed class RoadmapExtension : ISoloExtension
{
    public void ConfigureServices(IServiceCollection services, IExtensionContext context)
    {
        // The context pins its migrations history into the manifest's schema, so
        // the two disagreeing would put tables and history in different places.
        if (!string.Equals(context.Schema, RoadmapDbContext.Schema, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"extension.json declares schema \"{context.Schema}\" but {nameof(RoadmapDbContext)} " +
                $"is built for \"{RoadmapDbContext.Schema}\".");
        }

        services.AddExtensionDbContext<RoadmapDbContext>(context);
        services.AddEventHandler<TaskDeletedHandler>();
        services.AddScoped<RoadmapSettingsStore>();
    }
}
