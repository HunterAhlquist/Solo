using System.Net;
using Solo.Client.Api;
using Solo.Shared;

namespace Solo.Client.Tests;

public class ApiClientTests
{
    [Fact]
    public async Task Conflict_surfaces_as_ApiConflictException_carrying_the_ProblemDetails()
    {
        var http = new StubHttp().RespondConflict("Someone saved first.");

        var conflict = await Assert.ThrowsAsync<ApiConflictException>(
            () => http.CreateClient().SetTaskStatusAsync(Guid.NewGuid(), TaskState.Finished));

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("Concurrency conflict", conflict.Title);
        Assert.Equal("Someone saved first.", conflict.Detail);
    }

    [Fact]
    public async Task Other_failures_surface_as_ApiException_with_their_status()
    {
        var http = new StubHttp().Respond(HttpStatusCode.Forbidden, """{"title":"Forbidden"}""");

        var error = await Assert.ThrowsAsync<ApiException>(
            () => http.CreateClient().GetInstanceSettingsAsync());

        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.IsNotType<ApiConflictException>(error);
    }

    [Fact]
    public async Task A_non_json_error_body_still_produces_the_right_status()
    {
        var http = new StubHttp().Respond(HttpStatusCode.BadGateway, "<html>nginx</html>");

        var error = await Assert.ThrowsAsync<ApiException>(() => http.CreateClient().GetProjectsAsync());

        Assert.Equal(HttpStatusCode.BadGateway, error.StatusCode);
    }

    [Fact]
    public async Task Enums_are_sent_as_strings_because_the_API_uses_JsonStringEnumConverter()
    {
        var http = new StubHttp();
        http.RespondJson(TestData.Task(status: TaskState.InProgress));

        await http.CreateClient().SetTaskStatusAsync(TestData.BoardId, TaskState.InProgress);

        Assert.Contains("\"InProgress\"", http.Bodies[0]);
    }

    [Fact]
    public async Task Enums_are_read_back_from_strings()
    {
        var http = new StubHttp();
        http.RespondJson(TestData.Task(status: TaskState.Finished));

        var task = await http.CreateClient().GetTaskAsync(Guid.NewGuid());

        Assert.Equal(TaskState.Finished, task.Status);
    }

    [Fact]
    public async Task Rerank_sends_both_neighbours_so_the_server_can_pick_a_midpoint()
    {
        var http = new StubHttp();
        http.RespondJson(TestData.Task());
        var preceding = Guid.NewGuid();
        var following = Guid.NewGuid();

        await http.CreateClient().RerankTaskAsync(TestData.BoardId, preceding, following);

        Assert.Contains(preceding.ToString(), http.Bodies[0]);
        Assert.Contains(following.ToString(), http.Bodies[0]);
        Assert.Equal($"PUT /api/tasks/{TestData.BoardId}/rank", http.Paths[0]);
    }

    /// <summary>Labels live on the board; the project route holds only the defaults.</summary>
    [Fact]
    public async Task Label_queries_are_scoped_to_the_board_or_to_the_project_defaults()
    {
        var http = new StubHttp();
        http.RespondJson(Array.Empty<object>());
        http.RespondJson(Array.Empty<object>());

        var client = http.CreateClient();
        await client.GetBoardLabelsAsync(TestData.BoardId);
        await client.GetLabelsAsync(TestData.ProjectId);

        Assert.Equal($"GET /api/boards/{TestData.BoardId}/labels", http.Paths[0]);
        Assert.Equal($"GET /api/projects/{TestData.ProjectId}/labels", http.Paths[1]);
    }

    [Fact]
    public async Task Resetting_board_labels_posts_to_the_boards_reset_route()
    {
        var http = new StubHttp();
        http.RespondJson(Array.Empty<object>());

        await http.CreateClient().ResetBoardLabelsAsync(TestData.BoardId);

        Assert.Equal($"POST /api/boards/{TestData.BoardId}/labels/reset", http.Paths[0]);
    }
}
