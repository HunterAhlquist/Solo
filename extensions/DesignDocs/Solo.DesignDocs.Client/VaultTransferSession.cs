using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.JSInterop;
using Solo.DesignDocs.Contracts;
using Solo.Shared.Extensibility;

namespace Solo.DesignDocs.Client;

internal sealed class VaultTransferSession(ISoloClientApi api, IJSRuntime js, Guid projectId,
    string name, Func<string, Task> progress) : IAsyncDisposable
{
    private readonly CancellationTokenSource _cancel = new();
    private IJSObjectReference? _transfer;
    private DotNetObjectReference<VaultTransferSession>? _reference;
    private Guid _vaultId;
    private string Root => $"api/ext/design-docs/projects/{projectId}/vaults/{_vaultId}";

    private async Task<IJSObjectReference> Module()
    {
        if (_transfer is not null) return _transfer;
        var assets = new DesignDocsApi(api);
        var module = await DesignDocsAssets.ModuleAsync(js, assets);
        _reference = DotNetObjectReference.Create(this);
        return _transfer = await module.InvokeAsync<IJSObjectReference>("createTransfer", _reference, new { name });
    }

    public async Task<ImportSummary?> InspectAsync() =>
        await (await Module()).InvokeAsync<ImportSummary?>("inspect", _cancel.Token, new object?[] { Array.Empty<string>() });

    public async Task ImportAsync(Guid vaultId)
    {
        _vaultId = vaultId;
        await (await Module()).InvokeVoidAsync("import", _cancel.Token);
    }

    public async Task ExportAsync(TreeDto tree)
    {
        _vaultId = tree.Vault.Id;
        var current = await api.GetAsync<TreeDto>(Root, _cancel.Token);
        var images = await api.GetAsync<List<ImageDto>>(Root + "/images", _cancel.Token);
        var sketches = await api.GetAsync<List<SketchDto>>(Root + "/sketches", _cancel.Token);
        await (await Module()).InvokeVoidAsync("export", _cancel.Token, new
        {
            notes = current.Notes.Select(n => new { id = n.Id, path = NotePath(n, current.Folders) }), images, sketches,
        });
    }

    private static string NotePath(NoteDto note, IReadOnlyList<FolderDto> folders)
    {
        var parts = new List<string> { note.Name + ".md" };
        for (var id = note.FolderId; id.HasValue;)
        {
            var folder = folders.Single(f => f.Id == id);
            parts.Insert(0, folder.Name); id = folder.ParentId;
        }
        return string.Join('/', parts);
    }

    private async Task Retry(Func<Task> action)
    {
        for (var attempt = 0; ; attempt++)
        {
            _cancel.Token.ThrowIfCancellationRequested();
            try { await action(); return; }
            catch (SoloApiException e) when (e.StatusCode == HttpStatusCode.TooManyRequests && attempt < 6)
            {
                var wait = e.RetryAfter ?? TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt)));
                await Progress($"Waiting {Math.Ceiling(wait.TotalSeconds)} seconds before retrying…");
                await Task.Delay(wait, _cancel.Token);
            }
        }
    }

    [JSInvokable] public Task Progress(string text)
    {
        _cancel.Token.ThrowIfCancellationRequested(); return progress(text);
    }
    [JSInvokable] public Task Notes(string content) => Retry(async () =>
        await api.SendBytesAsync<JsonElement>(HttpMethod.Post, Root + "/import/notes", Encoding.UTF8.GetBytes(content), "application/json", _cancel.Token));
    [JSInvokable] public Task Image(string path, string type, string bytes) => Retry(async () =>
        await api.SendBytesAsync<ImageDto>(HttpMethod.Post, Root + "/images/import?path=" + Uri.EscapeDataString(path), Convert.FromBase64String(bytes), type, _cancel.Token));
    [JSInvokable] public Task<JsonElement> State(Guid id) =>
        api.GetAsync<JsonElement>($"api/ext/design-docs/projects/{projectId}/documents/{id}", _cancel.Token);
    [JSInvokable] public async Task<string> ImageBytes(Guid id) =>
        Convert.ToBase64String((await api.GetBytesAsync(Root + "/images/" + id, _cancel.Token)).Content);

    public void Cancel() => _cancel.Cancel();
    public async ValueTask DisposeAsync()
    {
        _cancel.Cancel();
        if (_transfer is not null)
        {
            try { await _transfer.InvokeVoidAsync("dispose"); await _transfer.DisposeAsync(); }
            catch (JSDisconnectedException) { }
        }
        _reference?.Dispose(); _cancel.Dispose();
    }
}
