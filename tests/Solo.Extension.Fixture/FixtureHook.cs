using Microsoft.AspNetCore.Http;
using Solo.Extensibility;

namespace Solo.Extension.Fixture;

/// <summary>
/// Drives every hook path from a request header, so one package exercises
/// proceed, veto, mutate, throw and hang without needing five.
/// </summary>
public sealed class FixtureHook(IHttpContextAccessor http) : ISoloOperationHook
{
    public const string BehaviourHeader = "X-Fixture-Hook";

    public IReadOnlySet<string> Operations { get; } = new HashSet<string>
    {
        Extensibility.Operations.TaskUpdate,
        Extensibility.Operations.TaskSetStatus,
        Extensibility.Operations.TaskSetSchedule,
        Extensibility.Operations.TaskSetBoard,
        Extensibility.Operations.TaskSetAssignees,
        Extensibility.Operations.EpicSetAssignees,
    };

    public int Order => 10;

    public async ValueTask<HookOutcome> OnBeforeAsync(OperationContext context, CancellationToken ct)
    {
        var behaviour = http.HttpContext?.Request.Headers[BehaviourHeader].ToString();
        switch (behaviour)
        {
            case "reject":
                return HookOutcome.Reject(409, "Blocked by fixture", "The fixture hook rejected this operation.");

            case "throw":
                throw new InvalidOperationException("Fixture hook failed on purpose.");

            case "hang":
                await Task.Delay(Timeout.Infinite, ct);
                return HookOutcome.Proceed;

            case "mutate":
                // Proves a Before hook can rewrite the bound request the action
                // then receives — later hooks and the action see the change.
                if (context.Arguments.TryGetValue("request", out var request) && request is not null)
                {
                    var name = request.GetType().GetProperty("Name");
                    if (name is not null && name.CanWrite)
                    {
                        name.SetValue(request, "mutated-by-hook");
                    }
                }

                return HookOutcome.Proceed;

            default:
                return HookOutcome.Proceed;
        }
    }
}
