using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Solo.Shared;
using Solo.Shared.Dtos;
using Solo.Shared.Extensibility;

namespace Solo.Client.Api;

/// <summary>
/// One method per API endpoint, returning <c>Solo.Shared</c> DTOs directly — no
/// client-side model duplication, no codegen. Mutations send the DTO's
/// <c>Version</c> (Postgres xmin) for optimistic concurrency; a 409 surfaces as
/// <see cref="ApiConflictException"/>.
/// </summary>
public sealed partial class ApiClient(HttpClient http, InstanceConnection instance) : ISoloClientApi
{
    // -- The extension seam -------------------------------------------------
    // An extension's client assembly cannot reference this one, so it injects
    // ISoloClientApi and gets these. Implemented explicitly so the escape hatch
    // does not sit among the endpoint methods as though it were one of them.
    //
    // Deliberately this class rather than a second HttpClient: an extension then
    // shares core's credential, base address and ProblemDetails handling, and
    // cannot end up on a path that authenticates differently from the app it is
    // running inside.

    Uri ISoloClientApi.Resolve(string relativePath) =>
        new(http.BaseAddress!, relativePath.TrimStart('/'));

    Uri ISoloClientApi.ResolveExternal(string relativePath) => instance.ResolveExternal(relativePath);

    Task<T> ISoloClientApi.GetAsync<T>(string path, CancellationToken ct) => GetAsync<T>(path, ct);

    Task<T> ISoloClientApi.SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct) =>
        SendAsync<T>(method, path, body, ct);

    Task ISoloClientApi.SendAsync(HttpMethod method, string path, object? body, CancellationToken ct) =>
        SendAsync(method, path, body, ct);

    async Task<SoloBinary> ISoloClientApi.GetBytesAsync(string path, CancellationToken ct)
    {
        using var response = await http.GetAsync(path, ct);
        await EnsureSuccessAsync(response, ct);
        return new(await response.Content.ReadAsByteArrayAsync(ct), response.Content.Headers.ContentType?.MediaType);
    }

    async Task<T> ISoloClientApi.SendBytesAsync<T>(HttpMethod method, string path, byte[] content,
        string contentType, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path) { Content = new ByteArrayContent(content) };
        request.Content.Headers.ContentType = new(contentType);
        using var response = await http.SendAsync(request, ct);
        return await ReadAsync<T>(response, ct);
    }

    // -- Bootstrap ----------------------------------------------------------
    public Task<AuthConfigDto> GetAuthConfigAsync(CancellationToken ct = default) =>
        GetAsync<AuthConfigDto>("api/config/auth", ct);

    public Task<SetupStatusDto> GetSetupStatusAsync(CancellationToken ct = default) =>
        GetAsync<SetupStatusDto>("api/setup/status", ct);

    /// <summary>
    /// The instance's theme. Anonymous, and fetched during bootstrap, because
    /// the sign-in screen is themed too.
    /// </summary>
    public Task<ThemeDto> GetActiveThemeAsync(CancellationToken ct = default) =>
        GetAsync<ThemeDto>("api/themes/active", ct);

    /// <summary>The installed themes, for the admin picker. Super admin only.</summary>
    public Task<ThemeScanResultDto> GetThemesAsync(CancellationToken ct = default) =>
        GetAsync<ThemeScanResultDto>("api/themes", ct);

    /// <summary>Re-reads the themes volume so a newly dropped-in package is picked up.</summary>
    public Task<ThemeScanResultDto> RescanThemesAsync(CancellationToken ct = default) =>
        SendAsync<ThemeScanResultDto>(HttpMethod.Post, "api/themes/rescan", null, ct);

    /// <summary>
    /// The extensions this instance serves a client half for. Authenticated
    /// like everything else here — the inventory is not public — so it is
    /// fetched after sign-in rather than during boot (see
    /// <c>ExtensionBootstrapper</c>).
    /// </summary>
    public Task<List<ClientExtensionDto>> GetClientExtensionsAsync(CancellationToken ct = default) =>
        GetAsync<List<ClientExtensionDto>>("api/extensions", ct);

    /// <summary>
    /// An extension's client assembly, as the bytes <c>Assembly.Load</c> wants.
    /// The one call here that isn't JSON.
    /// </summary>
    public async Task<byte[]> GetExtensionAssemblyAsync(
        string extensionId, string fileName, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"api/extensions/{extensionId}/client/{fileName}", ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>Every extension this instance has seen, including ones whose package is gone.</summary>
    public Task<ExtensionAdminDto> GetExtensionsAsync(CancellationToken ct = default) =>
        GetAsync<ExtensionAdminDto>("api/extensions/admin", ct);

    /// <summary>
    /// Drops an extension's schema and its keys from core's field bags.
    /// Irreversible, and refused while the extension is still loaded.
    /// </summary>
    public Task PurgeExtensionAsync(string extensionId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"api/extensions/{extensionId}/data",
            new PurgeExtensionRequest { ConfirmExtensionId = extensionId }, ct);

    public Task<UserDto> GetCurrentUserAsync(CancellationToken ct = default) =>
        GetAsync<UserDto>("api/users/me", ct);

    /// <summary>
    /// Final setup step. Called by the SSO-authenticated operator; the static
    /// credentials in the body re-prove they are the one who ran the wizard.
    /// </summary>
    public Task<UserDto> CompleteSetupAsync(CompleteSetupRequest request, CancellationToken ct = default) =>
        SendAsync<UserDto>(HttpMethod.Post, "api/setup/complete", request, ct);

    // -- Instance settings --------------------------------------------------
    public Task<InstanceSettingsDto> GetInstanceSettingsAsync(CancellationToken ct = default) =>
        GetAsync<InstanceSettingsDto>("api/settings", ct);

    public Task<InstanceSettingsDto> UpdateInstanceSettingsAsync(
        UpdateInstanceSettingsRequest request, CancellationToken ct = default) =>
        SendAsync<InstanceSettingsDto>(HttpMethod.Put, "api/settings", request, ct);

    // -- Plumbing -----------------------------------------------------------
    private async Task<T> GetAsync<T>(string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, ct);
        return await ReadAsync<T>(response, ct);
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string url, object? body, CancellationToken ct)
    {
        using var request = BuildRequest(method, url, body);
        using var response = await http.SendAsync(request, ct);
        return await ReadAsync<T>(response, ct);
    }

    private async Task SendAsync(HttpMethod method, string url, object? body, CancellationToken ct)
    {
        using var request = BuildRequest(method, url, body);
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
    }

    private static HttpRequestMessage BuildRequest(HttpMethod method, string url, object? body)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType(), options: SoloJson.Options);
        }

        return request;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        await EnsureSuccessAsync(response, ct);
        var value = await response.Content.ReadFromJsonAsync<T>(SoloJson.Options, ct);
        return value ?? throw new ApiException(
            response.StatusCode, "Empty response", $"The API returned no body for a {typeof(T).Name}.");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var (title, detail) = await ReadProblemAsync(response, ct);
        var retry = response.Headers.RetryAfter;
        TimeSpan? delay = retry?.Delta ?? (retry?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
        throw response.StatusCode == HttpStatusCode.Conflict
            ? new ApiConflictException(title, detail) { RetryAfter = delay }
            : new ApiException(response.StatusCode, title, detail) { RetryAfter = delay };
    }

    internal static async Task<(string? Title, string? Detail)> ReadProblemAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = document.RootElement;
            return (
                root.TryGetProperty("title", out var title) ? title.GetString() : null,
                root.TryGetProperty("detail", out var detail) ? detail.GetString() : null);
        }
        catch (Exception e) when (e is JsonException or ArgumentException)
        {
            // Not a ProblemDetails body (a proxy error page, say). The status
            // code alone is what the caller gets.
            return (null, null);
        }
    }
}
