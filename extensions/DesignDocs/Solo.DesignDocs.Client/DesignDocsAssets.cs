using System.Runtime.CompilerServices;
using Microsoft.JSInterop;

namespace Solo.DesignDocs.Client;

// Load styles through JavaScript. HeadContent can replace the instance theme.
public static class DesignDocsAssets
{
    private static readonly ConditionalWeakTable<IJSRuntime, Task<IJSObjectReference>> Modules = new();

    public static Task<IJSObjectReference> ModuleAsync(IJSRuntime js, DesignDocsApi api)
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

    public static async Task EnsureStylesAsync(IJSRuntime js, DesignDocsApi api)
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
        }
    }

    private static async Task<IJSObjectReference> LoadAsync(IJSRuntime js, DesignDocsApi api)
    {
        var module = await js.InvokeAsync<IJSObjectReference>("import", api.Content("design-docs.js").ToString());
        await module.InvokeVoidAsync("ensureStyles", api.Content("design-docs.css").ToString());
        return module;
    }
}
