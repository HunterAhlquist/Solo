using System.Reflection;

namespace Solo.Api;

/// <summary>
/// This server's build version: the release version and commit CI stamps into
/// the image (see Solo.Api.csproj), or the SDK default with the local commit in
/// a development build.
/// </summary>
internal static class SoloVersion
{
    public static string Server { get; } =
        typeof(SoloVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(SoloVersion).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";
}
