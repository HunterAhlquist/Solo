using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Solo.Shared;
using Solo.Shared.Dtos;
using Solo.Shared.Extensibility;

namespace Solo.Client.Api;

public sealed partial class ApiClient
{
    // -- Images -------------------------------------------------------------

    /// <summary>Relative path the browser can put straight into an <c>&lt;img src&gt;</c>.</summary>
    public static string ImageUrl(Guid imageId) => $"api/images/{imageId}";

    /// <summary>
    /// Same-origin path for a user's SSO profile picture, proxied and cached by the
    /// API. Loading the provider CDN directly gets the browser rate-limited (429).
    /// </summary>
    public static string AvatarUrl(Guid userId) => $"api/users/{userId}/avatar";

    /// <summary>Uploads a custom icon image; the returned id becomes an entity's <c>ImageId</c>.</summary>
    public async Task<ImageAssetDto> UploadImageAsync(
        Stream content, string fileName, string contentType, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        var file = new StreamContent(content);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        form.Add(file, "file", fileName);
        using var response = await http.PostAsync("api/images", form, ct);
        return await ReadAsync<ImageAssetDto>(response, ct);
    }

    // -- Audit --------------------------------------------------------------
    public Task<PagedResult<AuditEntryDto>> GetAuditAsync(
        Guid projectId,
        string? table = null,
        Guid? userId = null,
        DateTime? from = null,
        DateTime? to = null,
        int page = 1,
        int pageSize = 50,
        CancellationToken ct = default)
    {
        var query = new List<string> { $"page={page}", $"pageSize={pageSize}" };
        if (!string.IsNullOrWhiteSpace(table))
        {
            query.Add($"table={Uri.EscapeDataString(table)}");
        }

        if (userId is not null)
        {
            query.Add($"userId={userId}");
        }

        if (from is not null)
        {
            query.Add($"from={Uri.EscapeDataString(from.Value.ToString("O"))}");
        }

        if (to is not null)
        {
            query.Add($"to={Uri.EscapeDataString(to.Value.ToString("O"))}");
        }

        return GetAsync<PagedResult<AuditEntryDto>>(
            $"api/projects/{projectId}/audit?{string.Join('&', query)}", ct);
    }
}
