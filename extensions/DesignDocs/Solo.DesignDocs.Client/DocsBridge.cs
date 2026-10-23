using System.Net;
using Microsoft.JSInterop;
using MudBlazor;
using Solo.DesignDocs.Contracts;
using Solo.Shared;
using Solo.Shared.Extensibility;
using Solo.Shared.Hubs;

namespace Solo.DesignDocs.Client;

/// <summary>What the bridge needs from the page it serves.</summary>
public interface IDocsHost
{
    Task OpenNoteAsync(Guid noteId, string? slug);
    Task OpenSketchAsync(Guid sketchId);
    Task UnresolvedAsync(string target);
    Task DocumentStatusAsync(Guid documentId, DocumentStatus status);
    Task DocumentResetAsync(Guid documentId, string reason);
    Task PresenceAsync(Guid documentId, IReadOnlyList<TopicPresenceEntry> present);
    Task DocumentEventAsync(Guid documentId, string kind);
    Task FocusChangedAsync(bool focused);
    Task ImportProgressAsync(int done, int total, string phase);
    Task SketchViewAsync(SketchViewState state);
    Task ImageAddedAsync(ImageSummaryDto image);
}

public sealed record DocumentStatus(bool Synced, bool Connected, bool CanWrite, int Unacked, bool Seeding);

public sealed record SketchViewState(double Scale, int Rotation);

public sealed record UploadResult(ImageSummaryDto? Image, int? RetryAfterMs, string? Error);

/// <summary>
/// The browser module's way back into .NET for one vault page: its documents'
/// live topics, the REST calls it needs, navigation and the host's UI. The
/// module never touches the network itself, so sign-in, reconnects and the
/// desktop proxy stay core's.
/// </summary>
public sealed class DocsBridge(
    Guid projectId,
    Guid vaultId,
    DocsApi api,
    ISoloLiveTopics live,
    ISoloTaskDrawer drawer,
    ISnackbar snackbar,
    Microsoft.AspNetCore.Components.NavigationManager navigation,
    IDocsHost host) : IAsyncDisposable
{
    /// <summary>The kinds the REST side publishes on a document's topic; everything else is the relay's.</summary>
    private static readonly HashSet<string> RestKinds =
    [
        DocsTopics.Changed, DocsTopics.Tree, DocsTopics.Closed, DocsTopics.Reopened, DocsTopics.Deleted,
        DocsTopics.Renamed, DocsTopics.Image, DocsTopics.Edited,
    ];

    private readonly Dictionary<Guid, IAsyncDisposable> _documents = [];
    private readonly Dictionary<Guid, HashSet<Guid>> _present = [];

    public IJSObjectReference? Session { get; set; }

    public bool Disposed { get; private set; }

    [JSInvokable]
    public async Task OpenDocument(Guid documentId)
    {
        if (_documents.ContainsKey(documentId) || Disposed)
        {
            return;
        }

        // Claimed before the await, so a second open while this one subscribes doesn't subscribe twice.
        var holder = new SubscriptionHolder();
        _documents[documentId] = holder;
        holder.Inner = await live.SubscribeAsync(projectId, DocsTopics.Document(documentId),
            message => OnMessageAsync(documentId, message),
            present => OnPresenceAsync(documentId, present));
        if (Disposed || !_documents.TryGetValue(documentId, out var current) || current != holder)
        {
            await holder.Inner.DisposeAsync();
        }
    }

    [JSInvokable]
    public async Task CloseDocument(Guid documentId)
    {
        _present.Remove(documentId);
        if (_documents.Remove(documentId, out var subscription))
        {
            await subscription.DisposeAsync();
        }
    }

    [JSInvokable]
    public async Task<string> Send(Guid documentId, string kind, string? data)
    {
        var result = await live.SendAsync(projectId, DocsTopics.Document(documentId), kind, data);
        return result.ToString();
    }

    [JSInvokable]
    public Task Status(Guid documentId, DocumentStatus status) => host.DocumentStatusAsync(documentId, status);

    [JSInvokable]
    public Task DocumentReset(Guid documentId, string reason) => host.DocumentResetAsync(documentId, reason);

    public Task SetPresenceAsync(Guid documentId, PresenceMode mode) =>
        _documents.ContainsKey(documentId)
            ? live.SetPresenceAsync(projectId, DocsTopics.Document(documentId), mode)
            : Task.CompletedTask;

    private async Task OnMessageAsync(Guid documentId, TopicMessage message)
    {
        if (RestKinds.Contains(message.Kind))
        {
            await host.DocumentEventAsync(documentId, message.Kind);
        }
        else if (Session is not null && !Disposed)
        {
            await Session.InvokeVoidAsync("receive", documentId.ToString(), message.Kind, message.Data);
        }
    }

    /// <summary>Who's here; anyone who left loses their cursor now rather than at the awareness timeout.</summary>
    private async Task OnPresenceAsync(Guid documentId, IReadOnlyList<TopicPresenceEntry> present)
    {
        var now = present.Where(p => p.Mode != PresenceMode.None).Select(p => p.UserId).ToHashSet();
        if (_present.TryGetValue(documentId, out var before) && Session is not null && !Disposed)
        {
            var gone = before.Except(now).Select(id => id.ToString()).ToArray();
            if (gone.Length > 0)
            {
                await Session.InvokeVoidAsync("peersGone", documentId.ToString(), gone);
            }
        }

        _present[documentId] = now;
        await host.PresenceAsync(documentId, present);
    }

    // -- REST ---------------------------------------------------------------

    [JSInvokable]
    public async Task<byte[]> FetchImage(Guid imageId) => (await api.ImageAsync(projectId, vaultId, imageId)).Content;

    [JSInvokable]
    public async Task<UploadResult> UploadImage(string path, byte[] bytes, int width, int height)
    {
        try
        {
            var image = await api.UploadImageAsync(projectId, vaultId, path, bytes, width, height);
            await host.ImageAddedAsync(image);
            return new UploadResult(image, null, null);
        }
        catch (SoloApiException e) when (e.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return new UploadResult(null, (int)(e.RetryAfter ?? TimeSpan.FromSeconds(5)).TotalMilliseconds, null);
        }
        catch (Exception e) when (e is SoloApiException or HttpRequestException)
        {
            return new UploadResult(null, null, e.Message);
        }
    }

    [JSInvokable]
    public async Task SaveCopy(Guid noteId, string markdown, List<NoteHeadingDto> headings, long seq)
    {
        // Past the copy's cap the note still edits; its copy just stops following.
        if (markdown.Length > DocsLimits.MarkdownCopy)
        {
            return;
        }

        await api.SaveCopyAsync(projectId, vaultId, noteId, new NoteCopyRequest
        {
            Markdown = markdown,
            Headings = headings.Take(DocsLimits.Headings).ToList(),
            Seq = seq,
        });
    }

    [JSInvokable]
    public Task<DocumentLogDto> GetLog(Guid documentId) => api.LogAsync(projectId, vaultId, documentId);

    [JSInvokable]
    public async Task<ImportResultDto?> ImportNotes(List<string> folders, List<ImportNote> notes)
    {
        try
        {
            return await api.ImportNotesAsync(projectId, vaultId, new ImportNotesRequest { Folders = folders, Notes = notes });
        }
        catch (Exception e) when (e is SoloApiException or HttpRequestException)
        {
            snackbar.Add($"Import: {e.Message}", Severity.Error);
            return null;
        }
    }

    [JSInvokable]
    public Task ImportProgress(int done, int total, string phase) => host.ImportProgressAsync(done, total, phase);

    [JSInvokable]
    public async Task<List<TaskLookupDto>> LookupTasks(List<string> ids)
    {
        var parsed = ids.Select(id => Guid.TryParse(id, out var g) ? g : (Guid?)null).OfType<Guid>().Distinct().Take(200).ToList();
        return parsed.Count == 0 ? [] : await api.LookupTasksAsync(parsed);
    }

    // -- Navigation and the host's UI -----------------------------------------

    [JSInvokable]
    public void Navigate(string url) => navigation.NavigateTo(url.TrimStart('/'));

    [JSInvokable]
    public Task OpenNote(Guid noteId, string? slug) => host.OpenNoteAsync(noteId, slug);

    [JSInvokable]
    public Task OpenTask(string taskId) =>
        Guid.TryParse(taskId, out var id) ? drawer.OpenAsync(id) : Task.CompletedTask;

    [JSInvokable]
    public Task OpenSketch(Guid sketchId) => host.OpenSketchAsync(sketchId);

    [JSInvokable]
    public Task Unresolved(string target) => host.UnresolvedAsync(target);

    [JSInvokable]
    public Task FocusChanged(bool focused) => host.FocusChangedAsync(focused);

    [JSInvokable]
    public void HeadingNotFound(string slug) =>
        snackbar.Add("That heading isn't in this note any more, so it opened at the top.", Severity.Info);

    [JSInvokable]
    public void Notify(string message, string severity) =>
        snackbar.Add(message, Enum.TryParse<Severity>(severity, ignoreCase: true, out var s) ? s : Severity.Info);

    [JSInvokable]
    public Task SketchView(SketchViewState state) => host.SketchViewAsync(state);

    public async ValueTask DisposeAsync()
    {
        Disposed = true;
        foreach (var subscription in _documents.Values.ToList())
        {
            await subscription.DisposeAsync();
        }

        _documents.Clear();
    }

    /// <summary>A subscription that may still be on its way when it's closed.</summary>
    private sealed class SubscriptionHolder : IAsyncDisposable
    {
        public IAsyncDisposable? Inner { get; set; }

        public ValueTask DisposeAsync() => Inner?.DisposeAsync() ?? ValueTask.CompletedTask;
    }
}
