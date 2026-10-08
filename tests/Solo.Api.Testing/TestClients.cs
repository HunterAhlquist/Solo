using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Solo.Shared.Dtos;
using Testcontainers.PostgreSql;
using Xunit;

namespace Solo.Api.Testing;

public static class TestClients
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>An HttpClient signed in as the given test persona.</summary>
    public static HttpClient As(
        this WebApplicationFactory<Program> factory, string sub, string email, string? name = null)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.SubHeader, sub);
        client.DefaultRequestHeaders.Add(TestAuthHandler.EmailHeader, email);
        client.DefaultRequestHeaders.Add(TestAuthHandler.NameHeader, name ?? sub);
        return client;
    }

    public static HttpClient AsSetupAccount(this SoloApiFactory factory)
    {
        var client = factory.CreateClient();
        var token = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{SoloApiFactory.SetupUsername}:{SoloApiFactory.SetupPassword}"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Setup", token);
        return client;
    }

    /// <summary>Runs the first-run handoff, making the client's persona the super admin.</summary>
    public static async Task<UserDto> CompleteSetupAsync(HttpClient ssoClient)
    {
        var response = await ssoClient.PostAsJsonAsync("/api/setup/complete", new
        {
            setupUsername = SoloApiFactory.SetupUsername,
            setupPassword = SoloApiFactory.SetupPassword,
        }, Json);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<UserDto>(Json))!;
    }

    public static async Task<T> GetJson<T>(this HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        await EnsureSuccess(response, "GET", url);
        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }

    public static async Task<T> PostJson<T>(this HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body, Json);
        await EnsureSuccess(response, "POST", url);
        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }

    public static async Task<T> PutJson<T>(this HttpClient client, string url, object body)
    {
        var response = await client.PutAsJsonAsync(url, body, Json);
        await EnsureSuccess(response, "PUT", url);
        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, string method, string url)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            var serverErrors = TestLogCollector.Drain();
            throw new InvalidOperationException(
                $"{method} {url} -> {(int)response.StatusCode} {response.StatusCode}: {body}\n{serverErrors}");
        }
    }
}
