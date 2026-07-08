using Microsoft.EntityFrameworkCore;
using Solo.Data.Entities;

namespace Solo.Data.Auditing;

/// <summary>
/// Maps entities being saved to their owning project so audit rows can be
/// scoped per project. Parents are looked up in the change tracker first
/// (covers cascade deletes, where the parent row is gone by query time) and
/// fall back to single batched database queries.
/// </summary>
internal static class ProjectScopeResolver
{
    public static Dictionary<object, Guid?> Resolve(DbContext context, IEnumerable<object> entities)
    {
        var list = entities.ToList();
        var result = new Dictionary<object, Guid?>(ReferenceEqualityComparer.Instance);

        // Parent ids we still need to chase, gathered in one pass.
        var neededTaskIds = new HashSet<Guid>();
        var neededEpicIds = new HashSet<Guid>();
        var neededBoardIds = new HashSet<Guid>();

        foreach (var entity in list)
        {
            switch (entity)
            {
                case SubTask s: neededTaskIds.Add(s.TaskId); break;
                case Comment { TaskId: { } tid }: neededTaskIds.Add(tid); break;
                case Comment { EpicId: { } eid }: neededEpicIds.Add(eid); break;
                case Assignment { TaskId: { } tid }: neededTaskIds.Add(tid); break;
                case Assignment { EpicId: { } eid }: neededEpicIds.Add(eid); break;
                case Epic e: neededBoardIds.Add(e.BoardId); break;
                case TaskItem t: neededBoardIds.Add(t.BoardId); break;
            }
        }

        var taskBoards = LookupMap<TaskItem>(context, neededTaskIds, t => t.Id, t => t.BoardId);
        var epicBoards = LookupMap<Epic>(context, neededEpicIds, e => e.Id, e => e.BoardId);

        foreach (var boardId in taskBoards.Values.Concat(epicBoards.Values))
        {
            neededBoardIds.Add(boardId);
        }

        var boardProjects = LookupMap<Board>(context, neededBoardIds, b => b.Id, b => b.ProjectId);

        foreach (var entity in list)
        {
            result[entity] = entity switch
            {
                Project p => p.Id,
                Board b => b.ProjectId,
                Label l => l.ProjectId,
                ProjectMember m => m.ProjectId,
                Epic e => ViaBoard(e.BoardId),
                TaskItem t => ViaBoard(t.BoardId),
                SubTask s => ViaTask(s.TaskId),
                Comment { TaskId: { } tid } => ViaTask(tid),
                Comment { EpicId: { } eid } => ViaEpic(eid),
                Assignment { TaskId: { } tid } => ViaTask(tid),
                Assignment { EpicId: { } eid } => ViaEpic(eid),
                _ => null,
            };
        }

        return result;

        Guid? ViaBoard(Guid boardId) =>
            boardProjects.TryGetValue(boardId, out var projectId) ? projectId : null;

        Guid? ViaTask(Guid taskId) =>
            taskBoards.TryGetValue(taskId, out var boardId) ? ViaBoard(boardId) : null;

        Guid? ViaEpic(Guid epicId) =>
            epicBoards.TryGetValue(epicId, out var boardId) ? ViaBoard(boardId) : null;
    }

    private static Dictionary<Guid, Guid> LookupMap<TEntity>(
        DbContext context,
        HashSet<Guid> ids,
        Func<TEntity, Guid> key,
        Func<TEntity, Guid> value)
        where TEntity : class
    {
        var map = new Dictionary<Guid, Guid>();
        if (ids.Count == 0)
        {
            return map;
        }

        // Tracked instances first — cascade-deleted parents are only available here.
        foreach (var entry in context.ChangeTracker.Entries<TEntity>())
        {
            var k = key(entry.Entity);
            if (ids.Contains(k))
            {
                map[k] = value(entry.Entity);
            }
        }

        var missing = ids.Where(id => !map.ContainsKey(id)).ToList();
        if (missing.Count > 0)
        {
            foreach (var entity in context.Set<TEntity>().AsNoTracking()
                         .Where(e => missing.Contains(EF.Property<Guid>(e, "Id"))).ToList())
            {
                map[key(entity)] = value(entity);
            }
        }

        return map;
    }
}
