using Solo.Data.Entities;
using Solo.Shared.Dtos;

namespace Solo.Api.Services;

/// <summary>Entity → DTO mapping; entities never cross the API boundary.</summary>
public static class DtoMapper
{
    public static UserDto ToDto(this User u) =>
        new(u.Id, u.Email, u.DisplayName, u.AvatarUrl, u.IsSuperAdmin);

    public static ProjectMemberDto ToDto(this ProjectMember m) =>
        new(m.Id, m.UserId, m.Email, m.User?.DisplayName, m.User?.AvatarUrl,
            m.Role, m.IsOwner, m.UserId is null);

    public static ProjectDto ToDto(this Project p) =>
        new(p.Id, p.Name, p.Alias, p.Icon, p.ImageId, p.Version,
            p.Members.Select(m => m.ToDto()).ToList());

    public static BoardDto ToDto(this Board b) =>
        new(b.Id, b.ProjectId, b.Name, b.Alias, b.Icon, b.ImageId, b.IsOpen, b.Version);

    public static EpicDto ToDto(this Epic e) =>
        new(e.Id, e.BoardId, e.Name, e.Description, e.Rank, e.Version,
            e.Assignments.Select(a => a.UserId).ToList(), e.GroupId);

    public static EpicGroupDto ToDto(this EpicGroup g) =>
        new(g.Id, g.BoardId, g.Name, g.Rank, g.Version);

    public static TaskDto ToDto(this TaskItem t) =>
        new(t.Id, t.BoardId, t.EpicId, t.Number, t.Name, t.LabelId, t.Description, t.Status,
            t.CreatedAt, t.DeadlineAt, t.Rank, t.Version,
            t.Assignments.Select(a => a.UserId).ToList(),
            t.SubTasks.OrderBy(s => s.Rank).Select(s => s.ToDto()).ToList());

    public static SubTaskDto ToDto(this SubTask s) =>
        new(s.Id, s.TaskId, s.Name, s.LabelId, s.IsFinished, s.Rank, s.Version);

    public static LabelDto ToDto(this Label l) =>
        new(l.Id, l.ProjectId, l.BoardId, l.Kind, l.Name, l.Color, l.Icon, l.ImageId, l.Version);

    public static CommentDto ToDto(this Comment c) =>
        new(c.Id, c.AuthorId, c.Author?.DisplayName, c.Author?.AvatarUrl, c.Body, c.CreatedAt,
            c.EpicId, c.TaskId, c.Version);

    public static ImageAssetDto ToDto(this ImageAsset i) =>
        new(i.Id, i.ContentType);

    public static InstanceSettingsDto ToDto(this InstanceSettings s) =>
        new(s.AuditRetentionDays, s.SetupCompleted, s.ThemeId, s.Version);
}
