using Solo.Shared.Dtos;

namespace Solo.Client.Api;

public sealed partial class ApiClient
{
    public Task<EpicGroupDto> CreateEpicGroupAsync(Guid boardId, string name) =>
        SendAsync<EpicGroupDto>(HttpMethod.Post, $"api/boards/{boardId}/groups", new CreateEpicGroupRequest { Name = name }, default);

    public Task<EpicGroupDto> UpdateEpicGroupAsync(EpicGroupDto group, string name) =>
        SendAsync<EpicGroupDto>(HttpMethod.Put, $"api/boards/{group.BoardId}/groups/{group.Id}",
            new UpdateEpicGroupRequest { Name = name, Version = group.Version }, default);

    public Task DeleteEpicGroupAsync(EpicGroupDto group) =>
        SendAsync(HttpMethod.Delete, $"api/boards/{group.BoardId}/groups/{group.Id}?version={group.Version}", null, default);

    public Task<EpicGroupDto> RerankEpicGroupAsync(EpicGroupDto group, Guid? preceding, Guid? following) =>
        SendAsync<EpicGroupDto>(HttpMethod.Put, $"api/boards/{group.BoardId}/groups/{group.Id}/rank",
            new RerankEpicGroupRequest { PrecedingId = preceding, FollowingId = following, Version = group.Version }, default);

    public Task<EpicDto> MoveEpicAsync(EpicDto epic, Guid? groupId, Guid? preceding, Guid? following) =>
        SendAsync<EpicDto>(HttpMethod.Put, $"api/epics/{epic.Id}/group",
            new MoveEpicRequest { GroupId = groupId, PrecedingId = preceding, FollowingId = following, Version = epic.Version }, default);
}
