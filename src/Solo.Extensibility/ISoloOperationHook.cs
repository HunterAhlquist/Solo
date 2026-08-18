using Solo.Shared;

namespace Solo.Extensibility;

/// <summary>
/// A hook on a core write path. Registered in DI by the extension; dispatched
/// by the host around any action carrying a matching <c>[SoloOperation]</c>.
/// </summary>
/// <remarks>
/// <para>Deliberately not a C# <c>event</c>. A multicast delegate returning
/// <see cref="ValueTask"/> only surfaces the last handler's task, so awaiting it
/// fire-and-forgets every other subscriber; the first handler to throw abandons
/// the rest of the invocation list; there is no ordering control; and handlers
/// get no DI scope, which they need for a scoped DbContext.</para>
///
/// <para>The asymmetry between the two halves is the safety story: Before may
/// veto and mutate, After may not fail the operation.</para>
/// </remarks>
public interface ISoloOperationHook
{
    /// <summary>
    /// Operations this hook runs on. Must be a subset of the manifest's
    /// <c>hooks</c> list — the host drops anything not declared there, so the
    /// admin page's "which extensions sit on this path" is never a lie.
    /// </summary>
    IReadOnlySet<string> Operations { get; }

    /// <summary>Ascending. Ties break by extension id, so ordering is stable across restarts.</summary>
    int Order => 0;

    /// <summary>
    /// Runs after authorization and model binding, before the action body.
    /// May reject, and may mutate <see cref="OperationContext.Arguments"/>.
    /// </summary>
    ValueTask<HookOutcome> OnBeforeAsync(OperationContext context, CancellationToken ct);

    /// <summary>
    /// Runs after the action produced a result. Cannot fail the operation: the
    /// core write has already committed and already broadcast by this point.
    /// </summary>
    ValueTask OnAfterAsync(OperationResult result, CancellationToken ct) => ValueTask.CompletedTask;
}

/// <summary>
/// A Before hook's verdict. Structured rather than exception-driven so a
/// rejection reaches the caller as ProblemDetails instead of a 500.
/// </summary>
public abstract record HookOutcome
{
    private HookOutcome() { }

    public static HookOutcome Proceed { get; } = new Continue();

    /// <summary>Rejects the operation. <paramref name="Status"/> should be 4xx.</summary>
    public static HookOutcome Reject(int status, string title, string detail) =>
        new Rejection(status, title, detail);

    public sealed record Continue : HookOutcome;

    public sealed record Rejection(int Status, string Title, string Detail) : HookOutcome;
}

/// <summary>What a hook is told about the call it is intercepting.</summary>
public sealed class OperationContext
{
    public required string Operation { get; init; }

    /// <summary>The signed-in caller.</summary>
    public required ExtensionCaller Caller { get; init; }

    /// <summary>
    /// Route ids resolved for this call, when present: project, board, epic,
    /// task, sub-task. A hook must not assume the entity exists — resource
    /// lookup happens inside the action, after this runs.
    /// </summary>
    public required OperationTarget Target { get; init; }

    /// <summary>
    /// The action's bound arguments by parameter name. Mutating a value here
    /// changes what the action receives; later hooks see the change.
    /// </summary>
    public required IDictionary<string, object?> Arguments { get; init; }

    /// <summary>The bound request body, when the action takes one.</summary>
    public T? Request<T>() where T : class =>
        Arguments.Values.OfType<T>().FirstOrDefault();
}

/// <summary>The outcome handed to After hooks.</summary>
public sealed class OperationResult
{
    public required string Operation { get; init; }

    public required ExtensionCaller Caller { get; init; }

    public required OperationTarget Target { get; init; }

    /// <summary>HTTP status the action produced.</summary>
    public required int StatusCode { get; init; }

    /// <summary>True when the action completed without an error status.</summary>
    public bool Succeeded => StatusCode is >= 200 and < 300;

    /// <summary>The response body the action produced, when it produced one.</summary>
    public object? Value { get; init; }
}

/// <summary>Identity and access of the caller, already resolved by core authorization.</summary>
public sealed record ExtensionCaller(
    Guid UserId,
    string? Email,
    string? DisplayName,
    bool IsSuperAdmin,
    ProjectRole? Role,
    bool IsProjectOwner);

/// <summary>Entity ids resolved from the route, any of which may be absent.</summary>
public sealed record OperationTarget(
    Guid? ProjectId = null,
    Guid? BoardId = null,
    Guid? EpicId = null,
    Guid? TaskId = null,
    Guid? SubTaskId = null);
