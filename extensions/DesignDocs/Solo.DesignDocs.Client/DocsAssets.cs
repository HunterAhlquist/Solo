using System.Runtime.CompilerServices;
using Microsoft.JSInterop;

namespace Solo.DesignDocs.Client;

/// <summary>
/// Loads the browser module (content/design-docs.js) once per runtime, and has
/// it add the stylesheet: through the module rather than HeadContent, which
/// holds one owner's markup at a time (see RoadmapAssets).
/// </summary>
public static class DocsAssets
{
    private static readonly ConditionalWeakTable<IJSRuntime, Task<IJSObjectReference>> Modules = new();

    public static Task<IJSObjectReference> ModuleAsync(IJSRuntime js, DocsApi api)
    {
        lock (Modules)
        {
            if (Modules.TryGetValue(js, out var loading) && !loading.IsFaulted && !loading.IsCanceled)
            {
                return loading;
            }

            loading = LoadAsync(js, api);
            Modules.AddOrUpdate(js, loading);
            return loading;
        }
    }

    /// <summary>For components that only need the styles; a failure leaves them unstyled rather than broken.</summary>
    public static async Task EnsureStylesAsync(IJSRuntime js, DocsApi api)
    {
        try
        {
            await ModuleAsync(js, api);
        }
        catch (JSException)
        {
        }
        catch (InvalidOperationException)
        {
            // A test host with no JS: nothing to style.
        }
    }

    private static async Task<IJSObjectReference> LoadAsync(IJSRuntime js, DocsApi api)
    {
        var module = await js.InvokeAsync<IJSObjectReference>("import", api.Content("design-docs.js").ToString());
        await module.InvokeVoidAsync("ensureStyles", api.Content("design-docs.css").ToString());
        return module;
    }
}
