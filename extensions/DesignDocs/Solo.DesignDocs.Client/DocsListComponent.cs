using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;
using Solo.DesignDocs.Contracts;
using Solo.Shared.Dtos;
using Solo.Shared.Extensibility;

namespace Solo.DesignDocs.Client;

/// <summary>
/// What every list of a project's vaults shares (sidebar, home section, the
/// project's Design Docs page): the list, kept current from this tab and from
/// the project's vault-list topic, and creating one for those who may.
/// </summary>
public abstract class DocsListComponent : ComponentBase, IAsyncDisposable
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

    protected VaultListDto? List { get; private set; }

    protected string? Error { get; private set; }

    protected bool ShowClosed { get; set; }

    protected DocsApi Api => new(Client);

    protected IEnumerable<VaultDto> Open => List?.Vaults.Where(v => !v.IsClosed) ?? [];

    protected IEnumerable<VaultDto> Closed => List?.Vaults.Where(v => v.IsClosed) ?? [];

    protected string Href(VaultDto vault) => $"docs/{Project.Alias}/{vault.Id}";

    protected override async Task OnInitializedAsync()
    {
        DocsEvents.VaultsChanged += OnVaultsChanged;
        _ = DocsAssets.EnsureStylesAsync(JS, Api);
        await LoadAsync();
        _live = await Live.SubscribeAsync(Project.Id, DocsTopics.Vaults, _ => ReloadAsync());
    }

    private async Task LoadAsync()
    {
        try
        {
            List = await Api.VaultsAsync(Project.Id);
            Error = null;
        }
        catch (Exception e) when (e is SoloApiException or HttpRequestException)
        {
            Error = "Couldn't load design docs.";
        }
    }

    protected Task ReloadAsync() => InvokeAsync(async () =>
    {
        await LoadAsync();
        StateHasChanged();
    });

    private void OnVaultsChanged(Guid projectId)
    {
        if (projectId == Project.Id)
        {
            _ = ReloadAsync();
        }
    }

    protected async Task CreateAsync()
    {
        var name = await DocsDialogs.AskNameAsync(Dialogs, $"New design doc in {Project.Name}", "", "Create");
        if (name is null)
        {
            return;
        }

        try
        {
            var created = await Api.CreateVaultAsync(Project.Id, name);
            DocsEvents.RaiseVaultsChanged(Project.Id);
            Navigation.NavigateTo($"docs/{Project.Alias}/{created.Id}");
        }
        catch (Exception e) when (e is SoloApiException or HttpRequestException)
        {
            Snackbar.Add(e.Message, Severity.Error);
        }
    }

    public async ValueTask DisposeAsync()
    {
        DocsEvents.VaultsChanged -= OnVaultsChanged;
        if (_live is not null)
        {
            await _live.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }
}
