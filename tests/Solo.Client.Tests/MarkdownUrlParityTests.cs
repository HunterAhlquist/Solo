using System.Text.Json;
using Solo.Client.Components;
namespace Solo.Client.Tests;
public sealed class MarkdownUrlParityTests
{
    [Fact]
    public void Core_and_extension_use_the_shared_URL_safety_cases()
    {
        using var cases=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"markdown-safe-urls.json")));
        foreach(var item in cases.RootElement.EnumerateArray())Assert.Equal(item.GetProperty("safe").GetBoolean(),SoloMarkdownPipeline.IsSafeUrl(item.GetProperty("url").GetString()));
    }
}
