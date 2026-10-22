using System.Net;
using Solo.Client.Api;
using Solo.Shared;

namespace Solo.Client.Tests;

public class ApiClientTests
{
    [Fact]
    public async Task Binary_calls_keep_bytes_media_type_and_problem_details()
    {
        byte[] bytes = [0, 1, 127, 128, 255];
        var http = new StubHttp().RouteBytes("/image", bytes).RespondJson(new { saved = true });
        Solo.Shared.Extensibility.ISoloClientApi api = http.CreateClient();
        var download = await api.GetBytesAsync("image");
        Assert.Equal(bytes, download.Content);
        Assert.Equal("application/octet-stream", download.ContentType);
        var reply = await api.SendBytesAsync<System.Text.Json.JsonElement>(HttpMethod.Post, "upload", [65, 66], "image/webp");
        Assert.True(reply.GetProperty("saved").GetBoolean());
        Assert.Equal("AB", http.Bodies[1]);
        Assert.Equal("image/webp", http.Requests[1].Content!.Headers.ContentType!.MediaType);
        http.RespondConflict("Image replaced by someone else.");
        var error = await Assert.ThrowsAsync<ApiConflictException>(() => api.GetBytesAsync("missing"));
        Assert.Equal("Image replaced by someone else.", error.Detail);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Error_contract_preserves_Retry_After_for_json_and_binary(bool binary, bool date)
    {
        var instance = new InstanceConnection("https://solo.test/");
        Solo.Shared.Extensibility.ISoloClientApi api = new ApiClient(new HttpClient(new RetryHandler(date)) { BaseAddress = instance.BaseAddress }, instance);
        var error = await Assert.ThrowsAsync<ApiException>(async () =>
        {
            if (binary) await api.GetBytesAsync("limited");
            else await api.GetAsync<object>("limited");
        });
        Assert.NotNull(error.RetryAfter);
        Assert.InRange(error.RetryAfter.Value.TotalSeconds, date ? 25 : 12, date ? 30 : 12);
    }

    private sealed class RetryHandler(bool date) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("{}") };
            response.Headers.RetryAfter = date
                ? new System.Net.Http.Headers.RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddSeconds(30))
                : new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(12));
            return Task.FromResult(response);
        }
    }

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

    /// <summary>A board transfer names the destination board and, optionally, a lane on it.</summary>
    [Fact]
    public async Task Moving_a_task_puts_the_destination_board_in_the_body_not_the_route()
    {
        var http = new StubHttp();
        var taskId = Guid.NewGuid();
        var destination = Guid.NewGuid();
        http.RespondJson(TestData.Task());

        await http.CreateClient().MoveTaskAsync(taskId, destination, TestData.EpicId);

        Assert.Equal($"PUT /api/tasks/{taskId}/board", http.Paths[0]);
        Assert.Contains(destination.ToString(), http.Bodies[0]);
        Assert.Contains(TestData.EpicId.ToString(), http.Bodies[0]);
    }

    [Fact]
    public async Task Moving_a_task_with_no_epic_sends_a_null_so_it_lands_ad_hoc()
    {
        var http = new StubHttp();
        http.RespondJson(TestData.Task());

        await http.CreateClient().MoveTaskAsync(Guid.NewGuid(), TestData.BoardId, null);

        Assert.Contains("\"epicId\":null", http.Bodies[0]);
    }
}
