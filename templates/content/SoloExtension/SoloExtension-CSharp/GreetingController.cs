using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Solo.Extensibility;

namespace SoloExtension_CSharp;

/// <summary>
/// Reachable at <c>/api/ext/solo-extension-id/greeting</c>: the host
/// prefixes every route with the extension's id, whatever <c>[Route]</c> says.
/// </summary>
[ApiController]
[Route("greeting")]
public sealed class GreetingController(ISoloQuery query) : ControllerBase
{
    // Plain [Authorize]: the route names no project, so a project policy would
    // have nothing to resolve and would deny. It still requires a provisioned
    // Solo user, not only a valid sign-in.
    [HttpGet]
    [Authorize]
    public async Task<Greeting> Get(CancellationToken ct)
    {
        var caller = await query.CallerAsync(ct);
        return new Greeting($"Hello, {caller.DisplayName ?? caller.Email ?? "there"}.");
    }
}
