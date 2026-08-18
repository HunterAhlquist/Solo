using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Solo.Extensibility;

namespace Solo.Extensions;

/// <summary>
/// Dispatches extension hooks around any core action carrying
/// <c>[SoloOperation]</c>.
/// </summary>
/// <remarks>
/// <para>Runs after authorization — <c>AuthorizationMiddleware</c> executes
/// before the MVC filter pipeline — so <c>ProjectAccessHandler</c> has already
/// decided by the time a Before hook fires. A hook can further restrict access;
/// it can never widen it.</para>
///
/// <para>Before hooks also run before the action's own resource lookup, so a
/// hook on <c>task.update</c> fires even when the task is about to 404. Hooks
/// must not assume the entity exists.</para>
/// </remarks>
public sealed class ExtensionHookFilter(
    ExtensionState state,
    HookCatalog catalog,
    IOptions<ExtensionOptions> options,
    ILogger<ExtensionHookFilter> logger) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var operation = context.ActionDescriptor.EndpointMetadata
            .OfType<SoloOperationAttribute>().FirstOrDefault()?.Operation;
        if (operation is null)
        {
            // Not a hookable action: no lookup, no allocation, no cost.
            await next();
            return;
        }

        var hooks = catalog.For(context.HttpContext.RequestServices, operation, state);
        if (hooks.Count == 0)
        {
            await next();
            return;
        }

        var accessor = context.HttpContext.RequestServices.GetRequiredService<IExtensionCallerAccessor>();
        var (caller, projectId) = await accessor.CurrentAsync(
            context.HttpContext, context.HttpContext.RequestAborted);
        var target = HookContextFactory.Target(context, projectId);
        var operationContext = new OperationContext
        {
            Operation = operation,
            Caller = caller,
            Target = target,
            Arguments = context.ActionArguments,
        };

        foreach (var entry in hooks)
        {
            var outcome = await RunBeforeAsync(entry, operationContext, context.HttpContext.RequestAborted);
            if (outcome is HookOutcome.Rejection rejection)
            {
                context.Result = Problem(rejection, entry.ExtensionId);
                return;
            }
        }

        var executed = await next();

        var result = new OperationResult
        {
            Operation = operation,
            Caller = caller,
            Target = target,
            StatusCode = StatusFrom(executed),
            Value = (executed.Result as ObjectResult)?.Value,
        };

        foreach (var entry in hooks)
        {
            await RunAfterAsync(entry, result);
        }
    }

    /// <summary>
    /// Runs one Before hook under its timeout, converting a throw or an overrun
    /// into a rejection unless the extension opted into failing open.
    /// </summary>
    /// <remarks>
    /// Fail closed is the default and the important half: a hook that declared
    /// intent to veto and then broke must not be silently ignored, or an
    /// operator's installed policy stops applying without anyone noticing. Same
    /// reasoning as core's fallback authorization policy.
    /// </remarks>
    private async Task<HookOutcome> RunBeforeAsync(
        HookEntry entry, OperationContext context, CancellationToken requestAborted)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        timeout.CancelAfter(options.Value.HookTimeout);

        try
        {
            return await entry.Hook.OnBeforeAsync(context, timeout.Token);
        }
        catch (OperationCanceledException) when (!requestAborted.IsCancellationRequested)
        {
            logger.LogError(
                "Extension '{Id}' hook on {Operation} exceeded {Timeout}.",
                entry.ExtensionId, context.Operation, options.Value.HookTimeout);
            return entry.FailOpen
                ? HookOutcome.Proceed
                : HookOutcome.Reject(StatusCodes.Status500InternalServerError, "Extension hook timed out",
                    $"The '{entry.ExtensionId}' extension did not answer in time, so the request was refused.");
        }
        catch (Exception e)
        {
            logger.LogError(e, "Extension '{Id}' hook on {Operation} threw.", entry.ExtensionId, context.Operation);
            return entry.FailOpen
                ? HookOutcome.Proceed
                : HookOutcome.Reject(StatusCodes.Status500InternalServerError, "Extension hook failed",
                    $"The '{entry.ExtensionId}' extension failed while checking this request, so it was refused.");
        }
    }

    /// <summary>
    /// Runs one After hook. Cannot fail the operation: the core write has
    /// already committed and already broadcast by the time this runs.
    /// </summary>
    private async Task RunAfterAsync(HookEntry entry, OperationResult result)
    {
        using var timeout = new CancellationTokenSource(options.Value.NotificationTimeout);
        try
        {
            await entry.Hook.OnAfterAsync(result, timeout.Token);
        }
        catch (Exception e)
        {
            logger.LogError(
                e, "Extension '{Id}' after-hook on {Operation} threw; ignored.",
                entry.ExtensionId, result.Operation);
        }
    }

    private static ObjectResult Problem(HookOutcome.Rejection rejection, string extensionId)
    {
        var problem = new ProblemDetails
        {
            Status = rejection.Status,
            Title = rejection.Title,
            Detail = rejection.Detail,
        };
        // Naming the extension is what makes "why can't I move this card?"
        // answerable without reading the server log.
        problem.Extensions["extension"] = extensionId;
        return new ObjectResult(problem) { StatusCode = rejection.Status };
    }

    private static int StatusFrom(ActionExecutedContext executed)
    {
        if (executed.Exception is not null && !executed.ExceptionHandled)
        {
            return StatusCodes.Status500InternalServerError;
        }

        return executed.Result switch
        {
            ObjectResult { StatusCode: { } status } => status,
            StatusCodeResult status => status.StatusCode,
            _ => executed.HttpContext.Response.StatusCode,
        };
    }
}
