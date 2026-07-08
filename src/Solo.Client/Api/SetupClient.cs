using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Solo.Shared.Dtos;

namespace Solo.Client.Api;

/// <summary>
/// The two wizard steps authenticated by the static setup account rather than
/// SSO. They carry <c>Authorization: Setup base64(user:pass)</c>, so they need a
/// client without the ID-token handler. The scheme dies the moment setup
/// completes.
/// </summary>
public sealed class SetupClient(HttpClient http)
{
    /// <summary>Confirms the static setup credentials. False = wrong credentials, or setup is already done.</summary>
    public async Task<bool> VerifyAsync(string username, string password, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Post, "api/setup/verify", username, password, null, ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        return true;
    }

    public async Task<InstanceSettingsDto> UpdateSettingsAsync(
        string username, string password, int auditRetentionDays, CancellationToken ct = default)
    {
        var body = new SetupSettingsRequest { AuditRetentionDays = auditRetentionDays };
        using var response = await SendAsync(HttpMethod.Put, "api/setup/settings", username, password, body, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<InstanceSettingsDto>(SoloJson.Options, ct))!;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string url, string username, string password, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Setup", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}")));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType(), options: SoloJson.Options);
        }

        return await http.SendAsync(request, ct);
    }
}
