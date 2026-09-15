using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Solo.Client.Api;
using Solo.Client.State;
using Solo.Shared;

namespace Solo.Client.Tests;

/// <summary>
/// The 409 contract from the plan: refetch, replace local state, and tell the
/// user once. Snackbar, not modal — with presence warnings this should be rare.
/// </summary>
public class SaveGuardTests : ComponentTestBase
{
    private readonly SaveGuard _guard;
    private readonly ISnackbar _snackbar;

    public SaveGuardTests()
    {
        _snackbar = Services.GetRequiredService<ISnackbar>();
        _guard = new SaveGuard(_snackbar);
    }


    [Fact]
    public async Task A_successful_save_reports_success_and_says_nothing()
    {
        var ran = false;

        var ok = await _guard.RunAsync(() =>
        {
            ran = true;
            return Task.CompletedTask;
        });

        Assert.True(ok);
        Assert.True(ran);
        Assert.Empty(_snackbar.ShownSnackbars);
    }

    [Fact]
    public async Task A_conflict_refreshes_local_state_and_notifies_once()
    {
        var refreshed = false;

        var ok = await _guard.RunAsync(
            () => throw new ApiConflictException("Concurrency conflict", "The item was modified by someone else."),
            () =>
            {
                refreshed = true;
                return Task.CompletedTask;
            });

        Assert.False(ok);
        Assert.True(refreshed);
        Assert.Single(_snackbar.ShownSnackbars);
    }

    [Fact]
    public async Task A_conflict_prefers_the_servers_human_readable_detail()
    {
        await _guard.RunAsync(
            () => throw new ApiConflictException("Status is derived", "This task has sub-tasks."),
            () => Task.CompletedTask);

        Assert.Contains("This task has sub-tasks.", _snackbar.ShownSnackbars.Single().Message?.ToString() ?? string.Empty);
    }

    [Fact]
    public async Task Any_other_API_failure_surfaces_as_an_error()
    {
        var ok = await _guard.RunAsync(
            () => throw new ApiException(System.Net.HttpStatusCode.InternalServerError, "Server error", null));

        Assert.False(ok);
        Assert.Equal(Severity.Error, _snackbar.ShownSnackbars.Single().Severity);
    }

    [Fact]
    public async Task An_expired_session_says_so_instead_of_try_again()
    {
        var ok = await _guard.RunAsync(
            () => throw new ApiException(System.Net.HttpStatusCode.Unauthorized, "Unauthorized", null));

        Assert.False(ok);
        var shown = _snackbar.ShownSnackbars.Single();
        Assert.Equal(Severity.Warning, shown.Severity);
        Assert.Contains("session expired", shown.Message?.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_value_returning_save_hands_back_the_DTO_to_apply_optimistically()
    {
        var task = TestData.Task(status: TaskState.Finished);

        var result = await _guard.RunAsync(() => Task.FromResult(task));

        Assert.Equal(task.Id, result?.Id);
    }

    [Fact]
    public async Task A_value_returning_save_yields_null_on_conflict_so_nothing_is_applied()
    {
        var result = await _guard.RunAsync<object>(
            () => throw new ApiConflictException("Concurrency conflict", null),
            () => Task.CompletedTask);

        Assert.Null(result);
    }
}
