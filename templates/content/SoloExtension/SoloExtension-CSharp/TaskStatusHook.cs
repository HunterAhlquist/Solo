using Solo.Extensibility;

namespace SoloExtension_CSharp;

/// <summary>
/// Runs before core changes a task's status, and can refuse the change.
/// </summary>
/// <remarks>
/// <para>A hook only runs for operations the manifest lists under
/// <c>hooks</c>. One registered for anything else is dropped, so Admin settings
/// can say truthfully which extensions sit on a write path.</para>
///
/// <para>It runs after authorization, so it can narrow what a caller may do and
/// never widen it. A hook that throws, or takes longer than about two seconds,
/// fails the request unless the manifest sets <c>failOpen</c>.</para>
/// </remarks>
public sealed class TaskStatusHook : ISoloOperationHook
{
    public IReadOnlySet<string> Operations { get; } =
        new HashSet<string> { Solo.Extensibility.Operations.TaskSetStatus };

    public ValueTask<HookOutcome> OnBeforeAsync(OperationContext context, CancellationToken ct)
    {
        // The task may not exist: core looks it up inside the action, after
        // this runs.
        if (context.Target.TaskId is null)
        {
            return ValueTask.FromResult(HookOutcome.Proceed);
        }

        // To refuse the change, answer with a status and a reason instead:
        //
        //     return ValueTask.FromResult(
        //         HookOutcome.Reject(409, "Status locked", "This task's status can't change right now."));
        return ValueTask.FromResult(HookOutcome.Proceed);
    }
}
