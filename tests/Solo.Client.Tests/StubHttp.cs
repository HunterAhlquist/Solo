using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Solo.Client.Api;

namespace Solo.Client.Tests;

/// <summary>Records requests and replays canned responses, so ApiClient is exercised for real.</summary>
internal sealed class StubHttp : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses = new();
    private readonly List<(string Fragment, HttpMethod? Method, Func<HttpResponseMessage> Reply)> _routes = [];

    public List<HttpRequestMessage> Requests { get; } = [];

    /// <summary>
    /// Bodies are captured at send time: <see cref="ApiClient"/> disposes each
    /// request (and its content) as soon as the call returns.
    /// </summary>
    public List<string> Bodies { get; } = [];

    public List<string> Paths => Requests.Select(r => $"{r.Method} {r.RequestUri!.PathAndQuery}").ToList();

    public StubHttp Respond(HttpStatusCode status, string? json = null)
    {
        _responses.Enqueue(new HttpResponseMessage(status)
        {
            Content = new StringContent(json ?? string.Empty, Encoding.UTF8, "application/json"),
        });
        return this;
    }

    public StubHttp RespondJson(object value) =>
        Respond(HttpStatusCode.OK, System.Text.Json.JsonSerializer.Serialize(value, SoloJson.Options));

    /// <summary>A ProblemDetails 409, exactly as <c>ApiControllerBase.SaveOrConflictAsync</c> emits it.</summary>
    public StubHttp RespondConflict(string detail = "The item was modified by someone else.") =>
        Respond(HttpStatusCode.Conflict,
            $$"""{"title":"Concurrency conflict","detail":"{{detail}}","status":409}""");

    /// <summary>
    /// Answers any request whose path contains <paramref name="urlFragment"/>. Use
    /// this instead of the queue when a page fans out to several endpoints and the
    /// call order isn't the point of the test.
    /// </summary>
    public StubHttp RouteJson(string urlFragment, object value) =>
        Route(urlFragment, HttpStatusCode.OK, value);

    /// <summary>
    /// As <see cref="RouteJson"/>, but pinned to one verb and one status —
    /// needed where a page POSTs and GETs the same path, or where the point of
    /// the test is what happens on a 404.
    /// </summary>
    public StubHttp Route(
        string urlFragment, HttpStatusCode status, object? value = null, HttpMethod? method = null) =>
        Route(urlFragment, status, () => value, method);

    /// <summary>
    /// A route whose body is produced per call, for a page that reads the same
    /// endpoint again after changing it.
    /// </summary>
    public StubHttp Route(
        string urlFragment, HttpStatusCode status, Func<object?> value, HttpMethod? method = null)
    {
        _routes.Add((urlFragment, method, () =>
        {
            var current = value();
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(
                    current is null ? string.Empty : System.Text.Json.JsonSerializer.Serialize(current, SoloJson.Options),
                    Encoding.UTF8,
                    "application/json"),
            };
        }));
        return this;
    }

    /// <summary>
    /// A route whose status, as well as its body, is decided per call — for a
    /// resource that exists on one request and is gone on the next.
    /// </summary>
    public StubHttp Route(
        string urlFragment, Func<(HttpStatusCode Status, object? Value)> reply, HttpMethod? method = null)
    {
        _routes.Add((urlFragment, method, () =>
        {
            var (status, value) = reply();
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(
                    value is null ? string.Empty : System.Text.Json.JsonSerializer.Serialize(value, SoloJson.Options),
                    Encoding.UTF8,
                    "application/json"),
            };
        }));
        return this;
    }

    /// <summary>
    /// A route answering with raw bytes rather than JSON — an extension's client
    /// assembly is the one thing the app downloads that isn't a DTO.
    /// </summary>
    public StubHttp RouteBytes(string urlFragment, byte[] bytes)
    {
        _routes.Add((urlFragment, null, () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes)
            {
                Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") },
            },
        }));
        return this;
    }

    /// <summary>
    /// The instance the stub's clients are pointed at. Exposed so a test can
    /// drive <see cref="InstanceConnection.ResolveExternalAddressAsync"/> —
    /// standing in for the desktop shell — rather than reaching past it.
    /// </summary>
    public InstanceConnection Instance { get; } = new("https://solo.test/");

    public ApiClient CreateClient() =>
        new(new HttpClient(this) { BaseAddress = Instance.BaseAddress }, Instance);

    /// <summary>
    /// Awaited before every reply. Lets a test hold responses open and assert
    /// what the app shows while it waits — and, once released, that whatever
    /// redraws afterwards is the code under test rather than something else that
    /// happened to re-render in the meantime.
    /// </summary>
    public Func<Task>? Pause { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Pause is not null)
        {
            await Pause();
        }

        Requests.Add(request);
        Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));

        var path = request.RequestUri!.PathAndQuery;
        foreach (var (fragment, method, reply) in _routes)
        {
            if (path.Contains(fragment, StringComparison.Ordinal) &&
                (method is null || method == request.Method))
            {
                return reply();
            }
        }

        return _responses.Count > 0
            ? _responses.Dequeue()
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("null") };
    }
}
