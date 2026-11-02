using System.Net;
using System.Text.Json;
using Microsoft.JSInterop;
using MudBlazor;

namespace Solo.Client.State;

/// <summary>
/// The instance going down and coming back after an extension update, which
/// <see cref="Components.RestartOverlay"/> covers the whole app for.
/// </summary>
/// <remarks>
/// <para>Polls <c>api/ready</c> over a bare client, without the app's
/// credential or offline cache: the endpoint is anonymous, and a token on each
/// poll would cost the server a session lookup for nothing.</para>
///
/// <para>What to say once the app is back is kept in session storage, because
/// the reload that ends the restart discards everything else.</para>
/// </remarks>
public sealed class InstanceRestart(HttpClient http, IJSRuntime js)
{
    public const string HttpClientName = nameof(InstanceRestart);
    private const string NoticeKey = "solo.restart.notice";

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long before the overlay says the instance may not be coming back
    /// by itself — a host with no restart policy leaves it down.
    /// </summary>
    public TimeSpan SlowAfter { get; init; } = TimeSpan.FromMinutes(2);

    public bool InProgress { get; private set; }

    public event Action? Changed;

    /// <summary>What to tell the operator once the instance is back.</summary>
    public sealed record Notice(string Message, Severity Severity);

    /// <param name="notice">Shown once the app has reloaded, or null for nothing.</param>
    public async Task BeginAsync(Notice? notice)
    {
        if (notice is not null)
        {
            try
            {
                await js.InvokeVoidAsync("sessionStorage.setItem", NoticeKey, JsonSerializer.Serialize(notice));
            }
            catch (JSException)
            {
                // Storage refused (private mode, quota): the restart still happens,
                // it just goes unremarked after the reload.
            }
        }

        InProgress = true;
        Changed?.Invoke();
    }

    /// <summary>The notice left by <see cref="BeginAsync"/> before the last reload, once.</summary>
    public async Task<Notice?> TakeNoticeAsync()
    {
        try
        {
            var stored = await js.InvokeAsync<string?>("sessionStorage.getItem", NoticeKey);
            if (stored is null)
            {
                return null;
            }

            await js.InvokeVoidAsync("sessionStorage.removeItem", NoticeKey);
            return JsonSerializer.Deserialize<Notice>(stored);
        }
        catch (Exception e) when (e is JSException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Returns once the instance is serving again.</summary>
    /// <remarks>
    /// A 404 counts as back only after the instance has been seen down. The
    /// desktop shell can be newer than the instance it points at, and an
    /// instance from before <c>api/ready</c> answers it with the API's 404 —
    /// which, before the old process has gone, is no sign of the new one.
    /// </remarks>
    public async Task WaitUntilServingAsync(CancellationToken ct)
    {
        var sawDown = false;
        while (true)
        {
            await Task.Delay(PollInterval, ct);
            switch (await PollAsync(ct))
            {
                case HttpStatusCode.OK:
                case HttpStatusCode.NotFound when sawDown:
                    return;
                case HttpStatusCode.NotFound:
                    break;
                default:
                    sawDown = true;
                    break;
            }
        }
    }

    /// <summary>The ready endpoint's status, or 503 for no answer at all.</summary>
    private async Task<HttpStatusCode> PollAsync(CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync("api/ready", ct);
            return response.StatusCode;
        }
        catch (HttpRequestException)
        {
            return HttpStatusCode.ServiceUnavailable;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return HttpStatusCode.ServiceUnavailable;
        }
    }
}
