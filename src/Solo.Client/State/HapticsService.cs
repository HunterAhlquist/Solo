using Microsoft.JSInterop;
using Solo.Shared.Extensibility;

namespace Solo.Client.State;

/// <summary>
/// The host side of <see cref="ISoloHaptics"/>: hands the name to
/// <c>solo.haptics</c>, which passes it to the app shell if there is one.
/// Fire and forget, so a haptic never holds up the gesture it accompanies.
/// </summary>
public sealed class HapticsService(IJSRuntime js) : ISoloHaptics
{
    public void Play(string haptic) => _ = CallAsync("solo.haptics.play", haptic);

    public void Prepare() => _ = CallAsync("solo.haptics.prepare");

    // A test host or a page without the interop script has no shell to tell,
    // which is the same as a browser: nothing plays.
    private async Task CallAsync(string identifier, params object?[] args)
    {
        try
        {
            await js.InvokeVoidAsync(identifier, args);
        }
        catch (JSException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }
}
