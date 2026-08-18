using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Mvc.Authorization;

namespace Solo.Extensions;

/// <summary>
/// Forces every extension controller under <c>/api/ext/{extensionId}/</c>,
/// whatever its own <c>[Route]</c> says.
/// </summary>
/// <remarks>
/// <para>Enforced rather than trusted: without this an extension can declare
/// <c>[Route("api/tasks")]</c> and shadow a core endpoint. Rewriting the
/// template here means an extension's routing is a property of the host, not of
/// the package.</para>
///
/// <para>Applied only to controllers from extension application parts —
/// identified by their assembly — so core routing is untouched.</para>
/// </remarks>
public sealed class ExtensionRouteConvention(IReadOnlyDictionary<string, string> assemblyToExtensionId)
    : IApplicationModelConvention
{
    public void Apply(ApplicationModel application)
    {
        foreach (var controller in application.Controllers)
        {
            var assembly = controller.ControllerType.Assembly.GetName().Name;
            if (assembly is null || !assemblyToExtensionId.TryGetValue(assembly, out var extensionId))
            {
                continue;
            }

            var prefix = $"api/ext/{extensionId}";

            foreach (var selector in controller.Selectors.Where(s => s.AttributeRouteModel is not null))
            {
                selector.AttributeRouteModel!.Template = Combine(prefix, selector.AttributeRouteModel.Template);
            }

            foreach (var action in controller.Actions)
            {
                foreach (var selector in action.Selectors.Where(s => s.AttributeRouteModel is not null))
                {
                    // A controller-level route already carries the prefix; an
                    // action-level absolute route ("/api/whatever") would escape
                    // it, so it is rewritten too.
                    if (controller.Selectors.All(c => c.AttributeRouteModel is null))
                    {
                        selector.AttributeRouteModel!.Template =
                            Combine(prefix, selector.AttributeRouteModel.Template);
                    }
                }

                // Extension endpoints are project-scoped by default. An action
                // that wants a different policy still says so with its own
                // [Authorize]; this only guarantees none of them is anonymous
                // by omission, matching core's fallback policy.
                if (!action.Filters.OfType<AuthorizeFilter>().Any() &&
                    !action.Attributes.OfType<Microsoft.AspNetCore.Authorization.IAllowAnonymous>().Any() &&
                    !controller.Attributes.OfType<Microsoft.AspNetCore.Authorization.IAuthorizeData>().Any() &&
                    !action.Attributes.OfType<Microsoft.AspNetCore.Authorization.IAuthorizeData>().Any())
                {
                    action.Filters.Add(new AuthorizeFilter());
                }
            }
        }
    }

    private static string Combine(string prefix, string? template)
    {
        var trimmed = template?.TrimStart('/', '~') ?? string.Empty;
        return trimmed.Length == 0 ? prefix : $"{prefix}/{trimmed}";
    }
}
