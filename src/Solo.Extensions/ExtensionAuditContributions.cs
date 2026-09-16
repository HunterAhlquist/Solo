using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Solo.Extensibility;
using Solo.Shared.Dtos;

namespace Solo.Extensions;

/// <summary>Resolves enabled contributors and decorates authorized audit pages.</summary>
public sealed class ExtensionAuditContributions(
    IServiceProvider services, ExtensionState state, ILogger<ExtensionAuditContributions> logger)
{
    public IEnumerable<(string Id, ISoloAuditContributor Contributor)> Enabled()
    {
        foreach (var contributor in services.GetServices<ISoloAuditContributor>())
        {
            var package = state.Enabled.FirstOrDefault(p => p.LoadedAssembly == contributor.GetType().Assembly);
            if (package is not null) yield return (package.Id, contributor);
        }
    }

    public async Task<IReadOnlyList<AuditEntryDto>> AnnotateAsync(
        IReadOnlyList<AuditEntryDto> entries, CancellationToken ct)
    {
        if (entries.Count == 0) return entries;
        var labels = entries.ToDictionary(e => e.Id, _ => new Dictionary<string, string>());
        var ids = entries.Select(e => e.Id).ToList();
        foreach (var (id, contributor) in Enabled())
        {
            try
            {
                foreach (var (auditId, label) in await contributor.ReadAsync(ids, ct))
                {
                    if (labels.TryGetValue(auditId, out var row)) row[id] = label;
                }
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                logger.LogError(e, "Extension {ExtensionId} could not annotate the audit page.", id);
            }
        }
        return entries.Select(e => e with { Annotations = labels[e.Id] }).ToList();
    }
}
