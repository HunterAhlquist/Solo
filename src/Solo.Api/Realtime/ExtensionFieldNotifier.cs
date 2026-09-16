using Microsoft.EntityFrameworkCore;
using Solo.Data;
using Solo.Extensibility;
using Solo.Extensions;
using Solo.Shared;
using Solo.Shared.Hubs;

namespace Solo.Api.Realtime;

public sealed class ExtensionFieldNotifier(SoloDbContext db, IBoardNotifier notifier) : IExtensionFieldNotifier
{
    public async Task ChangedAsync(ExtensionEntity entity, Guid entityId)
    {
        var boardId = entity switch
        {
            ExtensionEntity.Task => await db.Tasks.Where(t => t.Id == entityId)
                .Select(t => (Guid?)t.BoardId).FirstOrDefaultAsync(),
            ExtensionEntity.Epic => await db.Epics.Where(t => t.Id == entityId)
                .Select(t => (Guid?)t.BoardId).FirstOrDefaultAsync(),
            ExtensionEntity.Board => entityId,
            _ => null,
        };
        if (boardId is null) return;
        var type = entity switch
        {
            ExtensionEntity.Task => EntityTypes.Task,
            ExtensionEntity.Epic => EntityTypes.Epic,
            _ => EntityTypes.Board,
        };
        await notifier.BoardEntityChanged(boardId.Value, type, entityId, ChangeKind.Updated, "Extensions");
    }
}
