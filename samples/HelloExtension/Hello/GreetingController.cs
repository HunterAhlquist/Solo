using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Solo.Extensibility;

namespace Hello;

/// <summary>
/// Reachable at <c>/api/ext/hello/greeting</c>: the host prefixes every route
/// with the extension's id, whatever <c>[Route]</c> says.
/// </summary>
[ApiController]
[Route("greeting")]
public sealed class GreetingController(ISoloQuery query) : ControllerBase
{
    // Plain [Authorize]: the route names no project, so a project policy would
    // have nothing to resolve and would deny.
    [HttpGet]
    [Authorize]
    public async Task<Greeting> Get(CancellationToken ct)
    {
        var caller = await query.CallerAsync(ct);
        return new Greeting($"Hello, {caller.DisplayName ?? caller.Email ?? "there"}.");
    }
}
