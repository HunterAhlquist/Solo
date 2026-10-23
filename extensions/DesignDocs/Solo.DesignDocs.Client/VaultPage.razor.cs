using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.JSInterop;
using MudBlazor;
using Solo.DesignDocs.Contracts;
using Solo.Shared;
using Solo.Shared.Dtos;
using Solo.Shared.Extensibility;
using Solo.Shared.Hubs;

namespace Solo.DesignDocs.Client;

public sealed partial class VaultPage : IDocsHost
{
    private const string ImageInputId = "dd-image-input";

    private (string Alias, Guid Vault)? _loadedFor;
    private ProjectSummaryDto? _project;
    private UserDto? _me;
    private VaultTreeDto? _tree;
    private string? _error;
    private Dictionary<Guid, int> _slots = [];
    private int _mySlot;

    private ElementReference _noteElement;
    private ElementReference _imageInput;
    private VaultTree? _treeView;

    private DocsBridge? _bridge;
    private DotNetObjectReference<DocsBridge>? _bridgeRef;
    private IJSObjectReference? _session;
    private Task? _starting;
    private IAsyncDisposable? _vaultLive;

    /// <summary>The note the address asks for, and the one the editor shows.</summary>
    private Guid? _noteId;
    private Guid? _mountedNoteId;
    private string? _wantedSlug;
    private NoteDto? _note;
    private DocumentStatus? _status;
    private IReadOnlyList<TopicPresenceEntry> _present = [];
    private bool _focused;

    private Guid? _sketchId;
    private IReadOnlyList<TopicPresenceEntry> _sketchPresent = [];
    private SketchViewState? _sketchState;

    private ImportProgressRelay? _import;

    [Inject] private ISoloClientApi Client { get; set; } = null!;
    [Inject] private ISoloLiveTopics Live { get; set; } = null!;
    [Inject] private ISoloTaskDrawer Drawer { get; set; } = null!;
    [Inject] private ISoloTaskPicker Picker { get; set; } = null!;
    [Inject] private ISoloContextMenu Menu { get; set; } = null!;
    [Inject] private IJSRuntime JS { get; set; } = null!;
    [Inject] private IDialogService Dialogs { get; set; } = null!;
    [Inject] private ISnackbar Snackbar { get; set; } = null!;
    [Inject] private NavigationManager Navigation { get; set; } = null!;

    [Parameter] public string ProjectAlias { get; set; } = "";

    [Parameter] public Guid VaultId { get; set; }

    [SupplyParameterFromQuery(Name = "note")] public Guid? Note { get; set; }

    private DocsApi Api => new(Client);

    private bool IsWriter => _project is { } p && (p.IsOwner || p.Role == ProjectRole.ReadWrite);

    /// <summary>Writing the vault's structure: a writer, and the vault open.</summary>
    private bool CanWrite => IsWriter && _tree is { Vault.IsClosed: false };

    /// <summary>Writing into the open note: also connected and in sync.</summary>
    private bool CanEdit => CanWrite && _status is not { Connected: false };

    private Guid? OpenNoteFolder => _tree?.Notes.FirstOrDefault(n => n.Id == _noteId)?.FolderId;

    private bool StatusWarns => _status is { Connected: false } || _status is { Unacked: > 0, Connected: true } && !_status.Synced;

    private string StatusText => _status switch
    {
        null => "",
        { Connected: false } => "Offline: read-only until reconnected",
        { Seeding: true } => "Preparing imported note…",
        { Synced: false } => "Connecting…",
        { Unacked: > 0 } => "Saving…",
        _ when !CanWrite => "Read-only",
        _ => "Saved",
    };

    // -- Loading ------------------------------------------------------------------

    protected override void OnInitialized()
    {
        Drawer.Changed += OnDrawerChanged;
        Live.ConnectionChanged += OnConnectionChangedAsync;
        Navigation.LocationChanged += OnLocationChanged;
    }

    protected override async Task OnParametersSetAsync()
    {
        _wantedSlug = SlugFromUri();
        if (_loadedFor is (var alias, var vault) &&
            string.Equals(alias, ProjectAlias, StringComparison.OrdinalIgnoreCase) && vault == VaultId)
        {
            _noteId = Note;
            return;
        }

        _loadedFor = (ProjectAlias, VaultId);
        await TeardownAsync();
        _noteId = Note;
        _error = null;
        _tree = null;
        var styles = DocsAssets.EnsureStylesAsync(JS, Api);
        await LoadAsync();
        await styles;
    }

    private async Task LoadAsync()
    {
        try
        {
            var me = Api.MeAsync();
            _project = await Api.ProjectAsync(ProjectAlias);
            _me = await me;
            if (_project is null)
            {
                _error = "That project doesn't exist, or you aren't a member of it.";
                return;
            }

            // Listening before the read, so a change made while it's on its way isn't missed.
            _vaultLive = await Live.SubscribeAsync(_project.Id, DocsTopics.Vault(VaultId), OnVaultMessageAsync);
            _tree = await Api.TreeAsync(_project.Id, VaultId);
            await LoadColorsAsync();
        }
        catch (SoloApiException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            _error = "That design doc doesn't exist. It may have been deleted.";
        }
        catch (Exception e) when (e is SoloApiException or HttpRequestException)
        {
            _error = "Couldn't load this design doc.";
        }
    }

    /// <summary>Everyone's color slots, claiming one for this person on their first visit to the project.</summary>
    private async Task LoadColorsAsync()
    {
        var colors = await Api.ColorsAsync(_project!.Id);
        if (_me is not null && colors.All(c => c.UserId != _me.Id))
        {
            colors.Add(await Api.ClaimColorAsync(_project.Id));
        }

        _slots = colors.ToDictionary(c => c.UserId, c => c.Slot);
        _mySlot = _me is not null && _slots.TryGetValue(_me.Id, out var slot) ? slot : 0;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_tree is null || _project is null || _error is not null)
        {
            return;
        }

        _starting ??= StartSessionAsync();
        await _starting;
        if (_session is not null && _noteId != _mountedNoteId)
        {
            await MountNoteAsync();
        }
    }

    private async Task StartSessionAsync()
    {
        try
        {
            var module = await DocsAssets.ModuleAsync(JS, Api);
            _bridge = new DocsBridge(_project!.Id, VaultId, Api, Live, Drawer, Snackbar, Navigation, this);
            _bridgeRef = DotNetObjectReference.Create(_bridge);
            _session = await module.InvokeAsync<IJSObjectReference>("createSession", _bridgeRef, new
            {
                userId = _me?.Id.ToString(),
                userName = _me?.DisplayName ?? _me?.Email ?? "Someone",
                colorSlot = _mySlot,
                canWrite = CanWrite,
                connected = Live.IsConnected,
                tree = _tree,
                fontUrl = Api.Content("silkscreen.woff2").ToString(),
            });
            _bridge.Session = _session;
            await _session.InvokeVoidAsync("attachImageInput", _imageInput);
            StateHasChanged();
        }
        catch (JSException e)
        {
            _error = $"The editor couldn't start: {e.Message}";
            StateHasChanged();
        }
    }

    // -- The open note ------------------------------------------------------------

    private async Task MountNoteAsync()
    {
        var wanted = _noteId;
        _mountedNoteId = wanted;
        _note = null;
        _status = null;
        _present = [];
        if (wanted is not { } noteId)
        {
            await _session!.InvokeVoidAsync("unmountNote");
            StateHasChanged();
            return;
        }

        if (_tree!.Notes.All(n => n.Id != noteId))
        {
            Snackbar.Add("That note isn't in this design doc any more.", Severity.Warning);
            CloseNote();
            return;
        }

        _treeView?.Reveal(noteId);
        var note = LoadNoteAsync(noteId);
        await _session!.InvokeAsync<bool>("mountNote", _noteElement, noteId.ToString(), _wantedSlug);
        _wantedSlug = null;
        if (_mountedNoteId == noteId && _bridge is not null)
        {
            await _bridge.SetPresenceAsync(noteId, PresenceMode.Viewing);
        }

        await note;
        StateHasChanged();
    }

    private async Task LoadNoteAsync(Guid noteId)
    {
        try
        {
            var note = await Api.NoteAsync(_project!.Id, VaultId, noteId);
            if (_mountedNoteId == noteId)
            {
                _note = note;
            }
        }
        catch (Exception e) when (e is SoloApiException or HttpRequestException)
        {
            // The header goes without "last edited"; the editor is what matters.
        }
    }

    private void GoToNote(Guid noteId, string? slug) =>
        Navigation.NavigateTo($"docs/{_project!.Alias}/{VaultId}?note={noteId}{(slug is null ? "" : $"#{slug}")}");

    private void CloseNote() => Navigation.NavigateTo($"docs/{_project!.Alias}/{VaultId}");

    private string? SlugFromUri()
    {
        var hash = Navigation.Uri.IndexOf('#');
        return hash >= 0 && hash < Navigation.Uri.Length - 1 ? Uri.UnescapeDataString(Navigation.Uri[(hash + 1)..]) : null;
    }

    /// <summary>A link to another heading of the open note changes only the fragment, which re-renders nothing.</summary>
    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        if (_session is not null && _mountedNoteId is not null && _mountedNoteId == Note && SlugFromUri() is { } slug)
        {
            _ = InvokeAsync(async () => await _session.InvokeAsync<bool>("scrollToHeading", slug));
        }
    }

    public Task OpenNoteAsync(Guid noteId, string? slug) => InvokeAsync(() => GoToNote(noteId, slug));

    // -- Live -------------------------------------------------------------------

    private Task OnVaultMessageAsync(TopicMessage message) => InvokeAsync(async () =>
    {
        switch (message.Kind)
        {
            case DocsTopics.Deleted:
                _error = "This design doc was deleted.";
                await TeardownAsync();
                break;
            case DocsTopics.Image when Guid.TryParse(message.Data, out var imageId):
                await ReloadTreeAsync();
                if (_session is not null)
                {
                    await _session.InvokeVoidAsync("forgetImage", imageId.ToString());
                }

                break;
            default:
                // Tree, closed, reopened, renamed: the tree carries all of it.
                await ReloadTreeAsync();
                break;
        }

        StateHasChanged();
    });

    private async Task ReloadTreeAsync()
    {
        if (_project is null)
        {
            return;
        }

        try
        {
            var wasWritable = CanWrite;
            _tree = await Api.TreeAsync(_project.Id, VaultId);
            if (_session is not null)
            {
                await _session.InvokeVoidAsync("setTree", _tree);
                if (wasWritable != CanWrite)
                {
                    await _session.InvokeVoidAsync("setAccess", CanWrite);
                }
            }

            if (_noteId is { } open && _tree.Notes.All(n => n.Id != open))
            {
                Snackbar.Add("The note you had open was deleted.", Severity.Info);
                CloseNote();
            }

            if (_sketchId is { } sketch && _tree.Sketches.All(s => s.Id != sketch))
            {
                _sketchId = null;
            }
        }
        catch (SoloApiException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            _error = "This design doc was deleted.";
            await TeardownAsync();
        }
        catch (Exception e) when (e is SoloApiException or HttpRequestException)
        {
            // The next nudge tries again.
        }
    }

    private Task OnConnectionChangedAsync() => InvokeAsync(async () =>
    {
        if (_session is not null)
        {
            await _session.InvokeVoidAsync("connection", Live.IsConnected);
        }

        if (Live.IsConnected)
        {
            // Anything published while the connection was down was missed.
            await ReloadTreeAsync();
            if (_mountedNoteId is { } noteId)
            {
                await LoadNoteAsync(noteId);
                await (_bridge?.SetPresenceAsync(noteId, _focused && CanWrite ? PresenceMode.Editing : PresenceMode.Viewing)
                    ?? Task.CompletedTask);
            }
        }

        StateHasChanged();
    });

    private void OnDrawerChanged(TaskDrawerChange change)
    {
        if (_session is not null)
        {
            _ = InvokeAsync(async () => await _session.InvokeVoidAsync("forgetTask", change.TaskId.ToString("N")));
        }
    }

    // -- IDocsHost ----------------------------------------------------------------

    public Task DocumentStatusAsync(Guid documentId, DocumentStatus status) => InvokeAsync(() =>
    {
        if (documentId == _mountedNoteId)
        {
            _status = status;
            StateHasChanged();
        }
    });

    public Task DocumentResetAsync(Guid documentId, string reason) => InvokeAsync(() =>
    {
        if (reason is "read-only" or "closed")
        {
            Snackbar.Add(reason == "closed"
                ? "This design doc was closed, so your last change wasn't saved."
                : "You can only read this project now, so your last change wasn't saved.", Severity.Warning);
        }
    });

    public Task PresenceAsync(Guid documentId, IReadOnlyList<TopicPresenceEntry> present) => InvokeAsync(() =>
    {
        var shown = present.Where(p => p.Mode != PresenceMode.None).ToList();
        if (documentId == _mountedNoteId)
        {
            _present = shown;
        }

        if (documentId == _sketchId)
        {
            _sketchPresent = shown;
        }

        StateHasChanged();
    });

    public Task DocumentEventAsync(Guid documentId, string kind) => InvokeAsync(async () =>
    {
        if (documentId == _mountedNoteId && kind == DocsTopics.Edited)
        {
            await LoadNoteAsync(documentId);
            StateHasChanged();
        }
        else if (kind is DocsTopics.Deleted or DocsTopics.Renamed or DocsTopics.Changed)
        {
            await ReloadTreeAsync();
            StateHasChanged();
        }
    });

    public async Task FocusChangedAsync(bool focused)
    {
        _focused = focused;
        if (_mountedNoteId is { } noteId && _bridge is not null && CanWrite)
        {
            await _bridge.SetPresenceAsync(noteId, focused ? PresenceMode.Editing : PresenceMode.Viewing);
        }
    }

    public Task ImportProgressAsync(int done, int total, string phase)
    {
        _import?.Report(done, total, phase);
        return Task.CompletedTask;
    }

    public Task SketchViewAsync(SketchViewState state) => InvokeAsync(() =>
    {
        _sketchState = state;
        StateHasChanged();
    });

    public Task ImageAddedAsync(ImageSummaryDto image) => InvokeAsync(async () =>
    {
        if (_tree is not null && _tree.Images.All(i => i.Id != image.Id))
        {
            _tree = _tree with { Images = [.. _tree.Images, image] };
            if (_session is not null)
            {
                await _session.InvokeVoidAsync("setTree", _tree);
            }
        }
    });

    public Task OpenSketchAsync(Guid sketchId) => InvokeAsync(() =>
    {
        _sketchId = sketchId;
        _sketchPresent = [];
        _sketchState = null;
        StateHasChanged();
    });

    private void CloseSketch()
    {
        _sketchId = null;
        _sketchPresent = [];
    }

    public Task UnresolvedAsync(string target) => InvokeAsync(async () =>
    {
        if (!CanWrite)
        {
            Snackbar.Add($"\"{target}\" doesn't exist yet.", Severity.Info);
            return;
        }

        if (!await DocsDialogs.ConfirmAsync(Dialogs, "Create note", $"\"{target}\" doesn't exist yet. Create it?", "Create"))
        {
            return;
        }

        // Obsidian puts a link's new note at the path the link names, when its folders exist.
        var segments = target.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var folder = FolderAt(segments[..^1]);
        await CreateNoteNamedAsync(folder, segments[^1]);
    });

    private Guid? FolderAt(IEnumerable<string> segments)
    {
        Guid? at = null;
        foreach (var segment in segments)
        {
            var next = _tree!.Folders.FirstOrDefault(f => f.ParentId == at &&
                string.Equals(f.Name, segment, StringComparison.OrdinalIgnoreCase));
            if (next is null)
            {
                return null;
            }

            at = next.Id;
        }

        return at;
    }

    // -- Tree actions -----------------------------------------------------------

    private async Task TreeMenuAsync(VaultTree.TreeMenuRequest request)
    {
        var items = new List<ContextMenuItem>();
        if (request.IsFolder)
        {
            if (!CanWrite)
            {
                return;
            }

            items.Add(new ContextMenuItem("note", "New note here") { Icon = Icons.Material.Filled.NoteAdd });
            items.Add(new ContextMenuItem("folder", "New folder here") { Icon = Icons.Material.Filled.CreateNewFolder });
            items.Add(new ContextMenuItem("rename", "Rename folder") { Icon = Icons.Material.Filled.Edit, SeparatorBefore = true });
            items.Add(new ContextMenuItem("move", "Move folder to…") { Icon = Icons.Material.Filled.DriveFileMove });
            items.Add(new ContextMenuItem("delete", "Delete folder and its notes…")
            {
                Icon = Icons.Material.Filled.Delete, Danger = true, SeparatorBefore = true,
            });
        }
        else
        {
            items.Add(new ContextMenuItem("open", "Open") { Icon = Icons.Material.Outlined.Description });
            items.Add(new ContextMenuItem("link", "Copy link") { Icon = Icons.Material.Filled.Link });
            if (CanWrite)
            {
                items.Add(new ContextMenuItem("rename", "Rename note") { Icon = Icons.Material.Filled.Edit, SeparatorBefore = true });
                items.Add(new ContextMenuItem("move", "Move note to…") { Icon = Icons.Material.Filled.DriveFileMove });
                items.Add(new ContextMenuItem("delete", "Delete note…")
                {
                    Icon = Icons.Material.Filled.Delete, Danger = true, SeparatorBefore = true,
                });
            }
        }

        var chosen = await Menu.ShowAsync(request.X, request.Y, items);
        switch (chosen, request.IsFolder)
        {
            case ("note", true): await CreateNoteAsync(request.Id); break;
            case ("folder", true): await CreateFolderAsync(request.Id); break;
            case ("rename", true): await RenameFolderAsync(request.Id); break;
            case ("move", true): await MoveFolderAsync(request.Id); break;
            case ("delete", true): await DeleteFolderAsync(request.Id); break;
            case ("open", false): GoToNote(request.Id, null); break;
            case ("link", false): await CopyLinkAsync(request.Id); break;
            case ("rename", false): await RenameNoteAsync(request.Id); break;
            case ("move", false): await MoveNoteAsync(request.Id); break;
            case ("delete", false): await DeleteNoteAsync(request.Id); break;
        }
    }

    private async Task CreateNoteAsync(Guid? folderId)
    {
        var name = await DocsDialogs.AskNameAsync(Dialogs, "New note", "Untitled", "Create");
        if (name is not null)
        {
            await CreateNoteNamedAsync(folderId, name);
        }
    }

    private async Task CreateNoteNamedAsync(Guid? folderId, string name)
    {
        await RunAsync(async () =>
        {
            var note = await Api.CreateNoteAsync(_project!.Id, VaultId, folderId, name);
            await ReloadTreeAsync();
            GoToNote(note.Id, null);
        });
    }

    private async Task CreateFolderAsync(Guid? parentId)
    {
        var name = await DocsDialogs.AskNameAsync(Dialogs, "New folder", "", "Create");
        if (name is not null)
        {
            await RunAsync(async () =>
            {
                await Api.CreateFolderAsync(_project!.Id, VaultId, parentId, name);
                await ReloadTreeAsync();
            });
        }
    }

    private async Task RenameFolderAsync(Guid folderId)
    {
        if (_tree!.Folders.FirstOrDefault(f => f.Id == folderId) is not { } folder)
        {
            return;
        }

        var name = await DocsDialogs.AskNameAsync(Dialogs, "Rename folder", folder.Name, "Rename");
        if (name is not null && name != folder.Name)
        {
            await RunAsync(() => Api.UpdateFolderAsync(_project!.Id, VaultId, folderId, name, folder.ParentId, folder.Version));
        }
    }

    private async Task MoveFolderAsync(Guid folderId)
    {
        if (_tree!.Folders.FirstOrDefault(f => f.Id == folderId) is not { } folder)
        {
            return;
        }

        var (chosen, target) = await DocsDialogs.PickFolderAsync(Dialogs, $"Move {folder.Name} to…", _tree, folder.ParentId, folder.Id);
        if (chosen)
        {
            await MoveAsync(new VaultTree.TreeMoveRequest(true, folderId, target));
        }
    }

    private async Task DeleteFolderAsync(Guid folderId)
    {
        if (_tree!.Folders.FirstOrDefault(f => f.Id == folderId) is not { } folder)
        {
            return;
        }

        if (await DocsDialogs.ConfirmAsync(Dialogs, "Delete folder",
                $"Delete \"{folder.Name}\" with every note, folder and sketch in it? This can't be undone.", "Delete"))
        {
            await RunAsync(() => Api.DeleteFolderAsync(_project!.Id, VaultId, folderId));
        }
    }

    private async Task RenameNoteAsync(Guid noteId)
    {
        if (_tree!.Notes.FirstOrDefault(n => n.Id == noteId) is not { } note)
        {
            return;
        }

        string? warning = null;
        try
        {
            var references = await Api.ReferencesAsync(_project!.Id, VaultId, noteId);
            if (references > 0)
            {
                warning = $"{references} {(references == 1 ? "link" : "links")} in other notes point here by name. " +
                          "After a rename they'll show as unresolved until someone updates them.";
            }
        }
        catch (Exception e) when (e is SoloApiException or HttpRequestException)
        {
        }

        var name = await DocsDialogs.AskNameAsync(Dialogs, "Rename note", note.Name, "Rename", warning: warning);
        if (name is not null && name != note.Name)
        {
            await RunAsync(async () =>
            {
                await Api.UpdateNoteAsync(_project!.Id, VaultId, noteId, name, note.FolderId, note.Version);
                if (_mountedNoteId == noteId)
                {
                    await LoadNoteAsync(noteId);
                }
            });
        }
    }

    private async Task MoveNoteAsync(Guid noteId)
    {
        if (_tree!.Notes.FirstOrDefault(n => n.Id == noteId) is not { } note)
        {
            return;
        }

        var (chosen, target) = await DocsDialogs.PickFolderAsync(Dialogs, $"Move {note.Name} to…", _tree, note.FolderId, null);
        if (chosen)
        {
            await MoveAsync(new VaultTree.TreeMoveRequest(false, noteId, target));
        }
    }

    private async Task MoveAsync(VaultTree.TreeMoveRequest move)
    {
        if (!CanWrite || _tree is null)
        {
            return;
        }

        if (move.IsFolder && _tree.Folders.FirstOrDefault(f => f.Id == move.Id) is { } folder && folder.ParentId != move.FolderId)
        {
            await RunAsync(() => Api.UpdateFolderAsync(_project!.Id, VaultId, folder.Id, folder.Name, move.FolderId, folder.Version));
        }
        else if (!move.IsFolder && _tree.Notes.FirstOrDefault(n => n.Id == move.Id) is { } note && note.FolderId != move.FolderId)
        {
            await RunAsync(() => Api.UpdateNoteAsync(_project!.Id, VaultId, note.Id, note.Name, move.FolderId, note.Version));
        }
    }

    private async Task DeleteNoteAsync(Guid noteId)
    {
        if (_tree!.Notes.FirstOrDefault(n => n.Id == noteId) is not { } note)
        {
            return;
        }

        if (await DocsDialogs.ConfirmAsync(Dialogs, "Delete note",
                $"Delete \"{note.Name}\" and its sketches? This can't be undone.", "Delete"))
        {
            await RunAsync(async () =>
            {
                await Api.DeleteNoteAsync(_project!.Id, VaultId, noteId);
                if (_noteId == noteId)
                {
                    CloseNote();
                }
            });
        }
    }

    private async Task CopyLinkAsync(Guid noteId)
    {
        var url = Client.ResolveExternal($"docs/note/{noteId:N}").ToString();
        try
        {
            await JS.InvokeVoidAsync("navigator.clipboard.writeText", url);
            Snackbar.Add("Link copied.", Severity.Success);
        }
        catch (JSException)
        {
            Snackbar.Add(url, Severity.Info);
        }
    }

    // -- Note toolbar -------------------------------------------------------------

    private async Task CreateSketchAsync()
    {
        if (_noteId is not { } noteId || _session is null)
        {
            return;
        }

        var dialog = await Dialogs.ShowAsync<SketchDialog>("New sketch", new DialogParameters<SketchDialog>
        {
            { d => d.InitialName, NextSketchName() },
        }, new DialogOptions { MaxWidth = MaxWidth.ExtraSmall, FullWidth = true });
        if (await dialog.Result is not { Canceled: false, Data: SketchDialog.SketchSettings settings })
        {
            return;
        }

        await RunAsync(async () =>
        {
            var sketch = await Api.CreateSketchAsync(_project!.Id, VaultId, new CreateSketchRequest
            {
                NoteId = noteId,
                Name = settings.Name,
                PageSize = settings.PageSize,
                Orientation = settings.Orientation,
                Pattern = settings.Pattern,
            });
            _tree = _tree! with { Sketches = [.. _tree.Sketches, sketch] };
            await _session.InvokeVoidAsync("setTree", _tree);
            await _session.InvokeAsync<bool>("insert", $"![[{sketch.Name}.sketch]]\n");
            await OpenSketchAsync(sketch.Id);
        });
    }

    private string NextSketchName()
    {
        var taken = _tree!.Sketches.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stem = _note?.Name is { } name ? $"{name} sketch" : "Sketch";
        for (var n = 1; ; n++)
        {
            var candidate = n == 1 ? stem : $"{stem} {n}";
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private async Task EditSketchAsync(SketchSummaryDto sketch)
    {
        var dialog = await Dialogs.ShowAsync<SketchDialog>("Sketch page", new DialogParameters<SketchDialog>
        {
            { d => d.Sketch, sketch },
            { d => d.SaveText, "Save" },
            { d => d.WouldClip, (size, orientation) =>
                _session!.InvokeAsync<bool>("sketchWouldClip", sketch.Id.ToString(), size, orientation).AsTask() },
        }, new DialogOptions { MaxWidth = MaxWidth.ExtraSmall, FullWidth = true });
        if (await dialog.Result is not { Canceled: false, Data: SketchDialog.SketchSettings settings })
        {
            return;
        }

        await RunAsync(() => Api.UpdateSketchAsync(_project!.Id, VaultId, sketch.Id, new UpdateSketchRequest
        {
            Name = settings.Name,
            PageSize = settings.PageSize,
            Orientation = settings.Orientation,
            Pattern = settings.Pattern,
            Version = sketch.Version,
        }));
    }

    private async Task DeleteSketchAsync(SketchSummaryDto sketch)
    {
        if (await DocsDialogs.ConfirmAsync(Dialogs, "Delete sketch",
                $"Delete \"{sketch.Name}\"? Notes that embed it will show the embed as missing.", "Delete"))
        {
            await RunAsync(async () =>
            {
                await Api.DeleteSketchAsync(_project!.Id, VaultId, sketch.Id);
                CloseSketch();
            });
        }
    }

    private async Task LinkTaskAsync()
    {
        if (_session is null || _project is null)
        {
            return;
        }

        var task = await Picker.PickAsync(_project.Id, new TaskPickerOptions { Title = "Link a task" });
        if (task is null)
        {
            return;
        }

        var serial = task.Name;
        try
        {
            serial = (await Api.LookupTasksAsync([task.Id])).FirstOrDefault()?.Serial ?? task.Name;
        }
        catch (Exception e) when (e is SoloApiException or HttpRequestException)
        {
        }

        await _session.InvokeAsync<bool>("insert", $"[{serial}](/docs/task/{task.Id:N})");
    }

    // -- Vault actions ------------------------------------------------------------

    private async Task ImportAsync()
    {
        if (_session is null)
        {
            return;
        }

        _import = new ImportProgressRelay();
        var dialog = await Dialogs.ShowAsync<ImportDialog>("Import from Obsidian", new DialogParameters<ImportDialog>
        {
            { d => d.Session, _session },
            { d => d.Progress, _import },
        }, new DialogOptions { MaxWidth = MaxWidth.Small, FullWidth = true, BackdropClick = false });
        await dialog.Result;
        _import = null;
        await ReloadTreeAsync();
    }

    private async Task ExportAsync()
    {
        if (_session is null || _tree is null)
        {
            return;
        }

        Snackbar.Add("Building the zip…", Severity.Info);
        try
        {
            await _session.InvokeAsync<int>("exportVault", $"{_tree.Vault.Name}.zip");
        }
        catch (JSException e)
        {
            Snackbar.Add($"The export failed: {e.Message}", Severity.Error);
        }
    }

    private async Task RenameVaultAsync()
    {
        var vault = _tree!.Vault;
        var name = await DocsDialogs.AskNameAsync(Dialogs, "Rename design doc", vault.Name, "Rename");
        if (name is not null && name != vault.Name)
        {
            await RunAsync(async () =>
            {
                await Api.RenameVaultAsync(_project!.Id, VaultId, name, vault.Version);
                DocsEvents.RaiseVaultsChanged(_project.Id);
            });
        }
    }

    private async Task CloseVaultAsync()
    {
        if (await DocsDialogs.ConfirmAsync(Dialogs, "Close design doc",
                "Closing makes it read-only for everyone. You can reopen it later.", "Close"))
        {
            await RunAsync(async () =>
            {
                await Api.CloseVaultAsync(_project!.Id, VaultId);
                DocsEvents.RaiseVaultsChanged(_project.Id);
            });
        }
    }

    private Task ReopenVaultAsync() => RunAsync(async () =>
    {
        await Api.ReopenVaultAsync(_project!.Id, VaultId);
        DocsEvents.RaiseVaultsChanged(_project.Id);
    });

    private async Task DeleteVaultAsync()
    {
        if (await DocsDialogs.ConfirmAsync(Dialogs, "Delete design doc",
                $"Delete \"{_tree!.Vault.Name}\" with all its notes, sketches and images? This can't be undone.", "Delete"))
        {
            await RunAsync(async () =>
            {
                await Api.DeleteVaultAsync(_project!.Id, VaultId);
                DocsEvents.RaiseVaultsChanged(_project.Id);
                Navigation.NavigateTo($"projects/{_project.Alias}");
            });
        }
    }

    /// <summary>A REST change, with its problem shown rather than thrown; the live nudge redraws the tree.</summary>
    private async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action();
            await ReloadTreeAsync();
        }
        catch (SoloApiException e) when (e.StatusCode == HttpStatusCode.Conflict)
        {
            await ReloadTreeAsync();
            Snackbar.Add(e.Detail ?? e.Message, Severity.Warning);
        }
        catch (Exception e) when (e is SoloApiException or HttpRequestException)
        {
            Snackbar.Add(e is SoloApiException api ? api.Detail ?? api.Message : e.Message, Severity.Error);
        }
    }

    private static string Ago(DateTimeOffset at)
    {
        var span = DateTimeOffset.UtcNow - at;
        return span.TotalMinutes < 1 ? "just now"
            : span.TotalHours < 1 ? $"{(int)span.TotalMinutes} min ago"
            : span.TotalDays < 1 ? $"{(int)span.TotalHours} h ago"
            : at.ToLocalTime().ToString("MMM d, yyyy");
    }

    // -- Teardown -----------------------------------------------------------------

    private async Task TeardownAsync()
    {
        if (_session is not null)
        {
            try
            {
                await _session.InvokeVoidAsync("destroy");
                await _session.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
            }
        }

        if (_bridge is not null)
        {
            await _bridge.DisposeAsync();
        }

        _bridgeRef?.Dispose();
        if (_vaultLive is not null)
        {
            await _vaultLive.DisposeAsync();
        }

        (_session, _bridge, _bridgeRef, _vaultLive, _starting) = (null, null, null, null, null);
        (_mountedNoteId, _note, _status, _present, _sketchId) = (null, null, null, [], null);
    }

    public async ValueTask DisposeAsync()
    {
        Drawer.Changed -= OnDrawerChanged;
        Live.ConnectionChanged -= OnConnectionChangedAsync;
        Navigation.LocationChanged -= OnLocationChanged;
        await TeardownAsync();
    }
}

/// <summary>Carries import progress from the bridge to the import dialog.</summary>
public sealed class ImportProgressRelay
{
    public event Action<int, int, string>? Changed;

    public void Report(int done, int total, string phase) => Changed?.Invoke(done, total, phase);
}
