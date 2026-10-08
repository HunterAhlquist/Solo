using Microsoft.Extensions.DependencyInjection;
using Solo.Extensibility;

namespace Solo.Extensions;

/// <summary>
/// The credential issuers of enabled extensions, each paired with its
/// extension id so a failure can name the extension it came from.
/// </summary>
public sealed class ExtensionCredentialIssuers(IServiceProvider services, ExtensionState state)
{
    public IEnumerable<(string Id, ISoloCredentialIssuer Issuer)> Enabled()
    {
        foreach (var issuer in services.GetServices<ISoloCredentialIssuer>())
        {
            var package = state.Enabled.FirstOrDefault(p => p.LoadedAssembly == issuer.GetType().Assembly);
            if (package is not null)
            {
                yield return (package.Id, issuer);
            }
        }
    }
}
