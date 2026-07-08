using System.Text.Json;
using System.Text.Json.Serialization;

namespace Solo.Client.Api;

/// <summary>
/// Serializer options for the REST API, which is configured with
/// <see cref="JsonStringEnumConverter"/> on the server side.
/// The SignalR hub deliberately does <em>not</em> use these — its protocol is
/// left at the ASP.NET default, where enums are numbers.
/// </summary>
public static class SoloJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
}
