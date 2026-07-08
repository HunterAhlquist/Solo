using MudBlazor;
using Solo.Client.Api;

namespace Solo.Client.State;

/// <summary>
/// Wraps a save event with the conflict rule from the plan: a 409 means someone
/// saved first, so refetch, replace local state, and say so once. With presence
/// warnings this should be rare — which is exactly why it gets a snackbar rather
/// than a modal.
/// </summary>
public sealed class SaveGuard(ISnackbar snackbar)
{
    /// <summary>
    /// Runs a save. Returns false when the save was rejected and the caller's
    /// local edit should be abandoned in favour of <paramref name="refresh"/>'s result.
    /// </summary>
    public async Task<bool> RunAsync(Func<Task> save, Func<Task>? refresh = null)
    {
        try
        {
            await save();
            return true;
        }
        catch (ApiConflictException e)
        {
            if (refresh is not null)
            {
                await refresh();
            }

            // The server distinguishes a stale-version conflict from a rule
            // conflict (e.g. dragging an auto-status task) by its title; the
            // detail is written for humans, so prefer it.
            snackbar.Add(
                e.Detail ?? "Updated by someone else — your view was refreshed.",
                Severity.Info);
            return false;
        }
        catch (ApiException e)
        {
            snackbar.Add(e.Detail ?? e.Title ?? "That didn't save. Try again.", Severity.Error);
            return false;
        }
    }

    /// <summary>Save events that produce a value — the DTO gets applied optimistically by the caller.</summary>
    public async Task<T?> RunAsync<T>(Func<Task<T>> save, Func<Task>? refresh = null)
        where T : class
    {
        T? result = null;

        // The braces matter: an expression-bodied lambda here would be inferred as
        // Func<Task<T>> and bind straight back to this overload — infinite recursion.
        await RunAsync(async () => { result = await save(); }, refresh);
        return result;
    }
}
