using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Solo.Shared.Extensibility;

namespace Solo.Client.Tests;

/// <summary>
/// Haptics as an extension page sees them through <see cref="ISoloHaptics"/>:
/// the name goes to <c>solo.haptics</c>, which hands it to the app shell, and
/// a page with no shell to hear it (a browser) carries on as if it had.
/// </summary>
public class HapticsTests : ComponentTestBase
{
    private ISoloHaptics Haptics => Services.GetRequiredService<ISoloHaptics>();

    [Fact]
    public void Play_hands_the_name_to_the_page_and_prepare_readies_it()
    {
        Haptics.Prepare();
        Haptics.Play(SoloHaptics.PickUp);
        Haptics.Play(SoloHaptics.Drop);

        Assert.Single(JSInterop.Invocations, i => i.Identifier == "solo.haptics.prepare");
        Assert.Equal(
            [SoloHaptics.PickUp, SoloHaptics.Drop],
            JSInterop.Invocations.Where(i => i.Identifier == "solo.haptics.play").Select(i => i.Arguments.Single()));
    }

    [Fact]
    public void A_page_without_the_interop_script_plays_nothing_and_doesnt_throw()
    {
        JSInterop.SetupVoid("solo.haptics.play", _ => true).SetException(new JSException("solo is not defined"));

        Haptics.Play(SoloHaptics.Explosion);
    }

    [Fact]
    public void The_page_and_the_iPhone_app_accept_exactly_these_names()
    {
        // Each side lists the names it lets through; one added to SoloHaptics
        // has to reach both, or it plays nothing.
        string[] names = [SoloHaptics.PickUp, SoloHaptics.Drop, SoloHaptics.Explosion];
        var root = FindRepositoryRoot();
        var interop = File.ReadAllText(Path.Combine(root, "src/Solo.Client/wwwroot/js/solo-interop.js"));
        var app = File.ReadAllText(Path.Combine(root, "src/Solo.Mobile/ios/App/App/Haptics.swift"));

        Assert.Contains("names: Object.freeze([" + string.Join(", ", names.Select(n => $"'{n}'")) + "])", interop);
        Assert.Contains("static let names: Set<String> = [" + string.Join(", ", names.Select(n => $"\"{n}\"")) + "]", app);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Solo.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Couldn't find the repository root above " + AppContext.BaseDirectory);
    }
}
