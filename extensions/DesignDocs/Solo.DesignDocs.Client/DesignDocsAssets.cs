using System.Runtime.CompilerServices;
using Microsoft.JSInterop;

namespace Solo.DesignDocs.Client;

/// <summary>
/// Loads the package's <c>content/design-docs.js</c> module, which in turn adds
/// <c>content/design-docs.css</c> to the page — once per runtime, whichever of this
/// package's components renders first.
/// </summary>
/// <remarks>
/// <para>The stylesheet goes in through the module rather than
/// <c>&lt;HeadContent&gt;</c>: HeadContent shows one owner's markup at a time,
/// so a sidebar section or a page using it would push the instance theme's
/// stylesheet out of the head.</para>
///
/// <para>Both URLs come from <see cref="DesignDocsApi.Content"/> — resolved for
/// <em>this</em> browser, which in the desktop shell is the local proxy — and
/// both are same-origin, so the CSP's <c>'self'</c> admits them as they are.</para>
/// </remarks>
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

    /// <summary>For components that only need the styles; a failure leaves them unstyled rather than broken.</summary>
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
            // Prerendering or a test host with no JS: nothing to style.
        }
    }

    private static async Task<IJSObjectReference> LoadAsync(IJSRuntime js, DesignDocsApi api)
    {
        var module = await js.InvokeAsync<IJSObjectReference>("import", api.Content("design-docs.js").ToString());
        await module.InvokeVoidAsync("ensureStyles", api.Content("design-docs.css").ToString());
        return module;
    }
}
