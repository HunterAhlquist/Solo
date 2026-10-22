using System.Reflection;
using Solo.Shared.Extensibility;

namespace Solo.DesignDocs.Client;

public sealed class DesignDocsApi(ISoloClientApi api)
{
    private static readonly string Version = Uri.EscapeDataString(typeof(DesignDocsApi).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown");
    public Uri Content(string file) => api.Resolve($"api/extensions/design-docs/content/{file}?v={Version}");
}
