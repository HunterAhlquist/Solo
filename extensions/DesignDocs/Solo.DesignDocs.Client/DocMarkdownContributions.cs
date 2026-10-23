using MudBlazor;
using Solo.Shared.Dtos;
using Solo.Shared.Extensibility;
using System.Runtime.CompilerServices;
namespace Solo.DesignDocs.Client;

[MarkdownEditorAction(Order=20)]
public sealed class DocLinkAction(IDialogService dialogs) : ISoloMarkdownEditorAction
{
    public ValueTask<IReadOnlyList<MarkdownEditorButton>> ButtonsAsync(MarkdownEditorContext context,CancellationToken ct)=>
        ValueTask.FromResult<IReadOnlyList<MarkdownEditorButton>>(context.ProjectId==Guid.Empty?[]:[new("doc","Link a doc"){Icon=Icons.Material.Filled.Description}]);
    public async Task<string?> OnChosenAsync(string buttonId,MarkdownEditorContext context,CancellationToken ct)
    {
        if(buttonId!="doc")return null;
        var d=await dialogs.ShowAsync<DocLinkPicker>("Link a doc",new DialogParameters<DocLinkPicker>{{x=>x.ProjectId,context.ProjectId}});
        using var registration=ct.Register(()=>d.Close());
        return await d.Result is {Canceled:false,Data:string markdown}?markdown:null;
    }
}

[MarkdownLinkProvider(Order=20)]
public sealed class DocLinkLabels(ISoloClientApi api) : ISoloMarkdownLinkProvider
{
    private sealed class Cache {public Dictionary<(Guid,string),(DateTimeOffset At,string Label)> Values {get;}=[];}
    private static readonly ConditionalWeakTable<ISoloClientApi,Cache> Caches=new();
    internal static void Invalidate(ISoloClientApi api)=>Caches.Remove(api);
    public bool Claims(string url)=>url.StartsWith("/docs/note/",StringComparison.Ordinal);
    public async ValueTask<IReadOnlyDictionary<string,string>> LabelsAsync(IReadOnlyList<string> urls,CancellationToken ct)
    {
        // Identity is part of the key: a WASM service scope outlives sign-out.
        var user=await api.GetAsync<UserDto>("api/users/me",ct);var cache=Caches.GetOrCreateValue(api);var now=DateTimeOffset.UtcNow;
        var result=new Dictionary<string,string>();var missing=new List<string>();
        lock(cache){foreach(var url in urls.Distinct()){if(cache.Values.TryGetValue((user.Id,url),out var v)&&now-v.At<TimeSpan.FromSeconds(30))result[url]=v.Label;else missing.Add(url);}}
        if(missing.Count>0){var fetched=await api.SendAsync<Dictionary<string,string>>(HttpMethod.Post,"api/ext/design-docs/links/labels",new Contracts.LabelsRequest(missing),ct);
            lock(cache){if(cache.Values.Count>1000)cache.Values.Clear();foreach(var pair in fetched){cache.Values[(user.Id,pair.Key)]=(now,pair.Value);result[pair.Key]=pair.Value;}}}
        return result;
    }
}
