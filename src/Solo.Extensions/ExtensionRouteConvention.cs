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

            var controllerRouted = controller.Selectors.Any(c => c.AttributeRouteModel is not null);
            foreach (var action in controller.Actions)
            {
                foreach (var selector in action.Selectors.Where(s => s.AttributeRouteModel is not null))
                {
                    // With no controller route, the action's template is the
                    // whole route and takes the prefix itself. With one, MVC
                    // combines the two — except for a template starting with
                    // "/" or "~/", which it reads as "ignore the controller's
                    // route" and uses verbatim. That is the one way out of the
                    // prefix from inside a routed controller, so such a template
                    // is prefixed here too. It keeps its leading slash: the
                    // author asked to bypass the controller's route, and that
                    // is still honoured, under the extension's namespace.
                    var template = selector.AttributeRouteModel!.Template;
                    if (!controllerRouted)
                    {
                        selector.AttributeRouteModel.Template = Combine(prefix, template);
                    }
                    else if (IsOverride(template))
                    {
                        selector.AttributeRouteModel.Template = "/" + Combine(prefix, template);
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

    /// <summary>
    /// The templates MVC takes as absolute: see
    /// <c>AttributeRouteModel.IsOverridePattern</c>, which this mirrors.
    /// </summary>
    private static bool IsOverride(string? template) =>
        template is not null &&
        (template.StartsWith('/') || template.StartsWith("~/", StringComparison.Ordinal));

    private static string Combine(string prefix, string? template)
    {
        var trimmed = template?.TrimStart('/', '~') ?? string.Empty;
        return trimmed.Length == 0 ? prefix : $"{prefix}/{trimmed}";
    }
}
