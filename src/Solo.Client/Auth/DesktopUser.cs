using System.Security.Claims;
using System.Text.Json;
using Solo.Shared.Dtos;

namespace Solo.Client.Auth;

/// <summary>
/// Builds the display principal for the desktop shell from the profile the
/// instance delivered alongside the session token (a <see cref="UserDto"/>
/// kept as JSON by solo-interop.js). Display only — the API authenticates
/// the session token itself on every request.
/// </summary>
public static class DesktopUser
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static ClaimsPrincipal? ToPrincipal(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        UserDto? user;
        try
        {
            user = JsonSerializer.Deserialize<UserDto>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }

        var claims = new List<Claim>();
        if (!string.IsNullOrEmpty(user?.DisplayName))
        {
            claims.Add(new Claim("name", user.DisplayName));
        }

        if (!string.IsNullOrEmpty(user?.Email))
        {
            claims.Add(new Claim("email", user.Email));
        }

        if (claims.Count == 0)
        {
            return null;
        }

        // Claim and identity naming match GoogleJwt.ToPrincipal, so the UI
        // reads Identity.Name the same way on both platforms.
        return new ClaimsPrincipal(
            new ClaimsIdentity(claims, "solo-session", nameType: "name", roleType: "role"));
    }
}
