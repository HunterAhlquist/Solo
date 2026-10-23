using System.Text.Json;
using Solo.Client.Components;

namespace Solo.DesignDocs.Tests;

/// <summary>
/// The editor's link rule (js/src/editor/markdown.js isSafeUrl) has to match
/// core's renderer, or a link core neutralizes in a task would be live in a
/// note. Both read the same table; this side checks core against it.
/// </summary>
public sealed class SafeUrlParityTests
{
    public static IEnumerable<object[]> Cases()
    {
        var table = JsonSerializer.Deserialize<JsonElement>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "safe-urls.json")));
        return table.GetProperty("cases").EnumerateArray()
            .Select(c => new object[] { c[0].GetString()!, c[1].GetBoolean() });
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Core_agrees_with_the_shared_table(string url, bool safe) =>
        Assert.Equal(safe, SoloMarkdownPipeline.IsSafeUrl(url));
}
