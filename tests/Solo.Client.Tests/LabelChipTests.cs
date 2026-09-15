using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Services;
using Solo.Client.Components;
using Solo.Client.Realtime;
using Solo.Client.State;
using Solo.Client.Theming;
using Solo.Shared;
using Solo.Shared.Dtos;
using Solo.Shared.Hubs;

namespace Solo.Client.Tests;

public class LabelChipTests : ComponentTestBase
{
    [Theory]
    [InlineData("#ecc94b", "#15151f")] // light label -> dark text
    [InlineData("#5b4bd6", "#ffffff")] // dark label  -> light text
    public void Text_colour_follows_the_labels_luminance(string background, string expectedText)
    {
        var chip = Render<LabelChip>(p => p.Add(c => c.Label, TestData.Label(Guid.NewGuid(), "L", background)));

        Assert.Contains($"color:{expectedText}", chip.Markup);
    }

    [Fact]
    public void No_label_renders_nothing()
    {
        var chip = Render<LabelChip>(p => p.Add(c => c.Label, null));

        Assert.Empty(chip.Markup.Trim());
    }
}
