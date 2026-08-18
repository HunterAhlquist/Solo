using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Solo.Data;
using Solo.Extensibility;

namespace Solo.Extensions;

/// <summary>
/// Per-extension custom fields on core entities, scoped so an extension can
/// only ever touch its own key.
/// </summary>
/// <remarks>
/// The extension id comes from the host at construction, never from the caller,
/// which is what makes "you can only read and write your own key" true rather
/// than merely intended.
/// </remarks>
public sealed class ExtensionFieldStore(SoloDbContext db, string extensionId) : IExtensionFieldStore
{
    /// <summary>Core tables carrying a bag. Also what a purge has to strip.</summary>
    internal static readonly string[] BagTables = ["Tasks", "Epics", "Boards"];

    public async Task<T?> GetAsync<T>(ExtensionEntity entity, Guid entityId, CancellationToken ct = default)
        where T : class
    {
        var raw = await ReadAsync(entity, entityId, ct);
        if (raw is null)
        {
            return null;
        }

        var node = JsonNode.Parse(raw)?[extensionId];
        return node is null ? null : node.Deserialize<T>();
    }

    public async Task SetAsync<T>(ExtensionEntity entity, Guid entityId, T value, CancellationToken ct = default)
        where T : class
    {
        var raw = await ReadAsync(entity, entityId, ct);
        var document = (raw is null ? null : JsonNode.Parse(raw)?.AsObject()) ?? new JsonObject();
        document[extensionId] = JsonSerializer.SerializeToNode(value);
        await WriteAsync(entity, entityId, document.ToJsonString(), ct);
    }

    public async Task ClearAsync(ExtensionEntity entity, Guid entityId, CancellationToken ct = default)
    {
        var raw = await ReadAsync(entity, entityId, ct);
        if (raw is null)
        {
            return;
        }

        var document = JsonNode.Parse(raw)?.AsObject();
        if (document is null || !document.Remove(extensionId))
        {
            return;
        }

        await WriteAsync(entity, entityId, document.Count == 0 ? null : document.ToJsonString(), ct);
    }

    private async Task<string?> ReadAsync(ExtensionEntity entity, Guid entityId, CancellationToken ct) =>
        entity switch
        {
            ExtensionEntity.Task => await db.Tasks.Where(t => t.Id == entityId)
                .Select(t => t.Extensions).FirstOrDefaultAsync(ct),
            ExtensionEntity.Epic => await db.Epics.Where(e => e.Id == entityId)
                .Select(e => e.Extensions).FirstOrDefaultAsync(ct),
            ExtensionEntity.Board => await db.Boards.Where(b => b.Id == entityId)
                .Select(b => b.Extensions).FirstOrDefaultAsync(ct),
            _ => throw new ArgumentOutOfRangeException(nameof(entity)),
        };

    /// <summary>
    /// Writes through the tracked entity rather than raw SQL so the change is
    /// audited like any other. Note the audit records the whole bag as one
    /// column change, not a per-key diff.
    /// </summary>
    private async Task WriteAsync(ExtensionEntity entity, Guid entityId, string? json, CancellationToken ct)
    {
        switch (entity)
        {
            case ExtensionEntity.Task:
                var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == entityId, ct);
                if (task is null) return;
                task.Extensions = json;
                break;
            case ExtensionEntity.Epic:
                var epic = await db.Epics.FirstOrDefaultAsync(e => e.Id == entityId, ct);
                if (epic is null) return;
                epic.Extensions = json;
                break;
            case ExtensionEntity.Board:
                var board = await db.Boards.FirstOrDefaultAsync(b => b.Id == entityId, ct);
                if (board is null) return;
                board.Extensions = json;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(entity));
        }

        await db.SaveChangesAsync(ct);
    }
}
