using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;
using Solo.Roadmap.Contracts;
using Solo.Shared.Dtos;
using Solo.Shared.Extensibility;

namespace Solo.Roadmap.Client;

/// <summary>
/// What every list of a project's roadmaps shares — the sidebar section, the
/// project home section and the project card's surface: the list itself, kept
/// current both from this browser (<see cref="RoadmapNavEvents"/>) and from
/// everyone else's (the project's live roadmap-list topic).
/// </summary>
public abstract class RoadmapListComponent : ComponentBase, IAsyncDisposable
{
    private IAsyncDisposable? _live;

    [Inject] protected ISoloClientApi Client { get; set; } = null!;
    [Inject] protected ISoloLiveTopics Live { get; set; } = null!;
    [Inject] protected IJSRuntime JS { get; set; } = null!;
    [Inject] protected IDialogService Dialogs { get; set; } = null!;
    [Inject] protected ISnackbar Snackbar { get; set; } = null!;
    [Inject] protected NavigationManager Navigation { get; set; } = null!;

    [Parameter, EditorRequired]
    public ProjectSummaryDto Project { get; set; } = null!;

    [Parameter]
    public bool CanWrite { get; set; }

    protected List<RoadmapSummaryDto>? Roadmaps { get; private set; }

    protected string? Error { get; private set; }

    protected RoadmapApi Api => new(Client);

    protected string Href(RoadmapSummaryDto roadmap) => $"projects/{Project.Alias}/roadmaps/{roadmap.Id}";

    protected override async Task OnInitializedAsync()
    {
        RoadmapNavEvents.Changed += OnRoadmapsChanged;
        _ = RoadmapAssets.EnsureStylesAsync(JS, Api);
        await LoadAsync();
        _live = await Live.SubscribeAsync(Project.Id, RoadmapTopics.List, _ => ReloadAsync());
    }

    private async Task LoadAsync()
    {
        try
        {
            Roadmaps = await Api.ListAsync(Project.Id);
            Error = null;
        }
        catch (Exception e) when (e is SoloApiException or HttpRequestException)
        {
            // A caption rather than a throw: this section's boundary would catch
            // it, but "couldn't load" says more than "failed" does.
            Error = "Couldn't load roadmaps.";
        }
    }

    protected Task ReloadAsync() => InvokeAsync(async () =>
    {
        await LoadAsync();
        StateHasChanged();
    });

    private void OnRoadmapsChanged(Guid projectId)
    {
        if (projectId == Project.Id)
        {
            _ = ReloadAsync();
        }
    }

    /// <summary>Asks for a title and creates the roadmap, then opens it.</summary>
    protected async Task CreateAsync()
    {
        var dialog = await Dialogs.ShowAsync<RoadmapTitleDialog>($"New roadmap in {Project.Name}");
        if (await dialog.Result is not { Canceled: false, Data: string title })
        {
            return;
        }

        try
        {
            var created = await Api.CreateAsync(Project.Id, title);
            RoadmapNavEvents.Raise(Project.Id);
            Navigation.NavigateTo($"projects/{Project.Alias}/roadmaps/{created.Id}");
        }
        catch (Exception e) when (e is SoloApiException or HttpRequestException)
        {
            Snackbar.Add(e.Message, Severity.Error);
        }
    }

    /// <summary>Renames over the version this list last read; a conflict reloads it and says so.</summary>
    protected async Task RenameAsync(RoadmapSummaryDto roadmap)
    {
        var dialog = await Dialogs.ShowAsync<RoadmapTitleDialog>("Rename roadmap",
            new DialogParameters<RoadmapTitleDialog>
            {
                { d => d.InitialTitle, roadmap.Title },
                { d => d.SaveText, "Rename" },
            });
        if (await dialog.Result is not { Canceled: false, Data: string title } || title == roadmap.Title)
        {
            return;
        }

        try
        {
            await Api.RenameAsync(Project.Id, roadmap.Id, title, roadmap.Version);
            RoadmapNavEvents.Raise(Project.Id);
        }
        catch (SoloApiException e) when (e.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            await ReloadAsync();
            Snackbar.Add("Someone else changed this roadmap first. It's been refreshed; try again.", Severity.Warning);
        }
        catch (Exception e) when (e is SoloApiException or HttpRequestException)
        {
            Snackbar.Add(e.Message, Severity.Error);
        }
    }

    public async ValueTask DisposeAsync()
    {
        RoadmapNavEvents.Changed -= OnRoadmapsChanged;
        if (_live is not null)
        {
            await _live.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }
}
