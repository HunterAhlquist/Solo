using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace Solo.Shared.Kit;

/// <summary>
/// The kit as plain HTML, for wherever no host has registered a renderer: an
/// extension's own component tests, mostly. Each element says which kit
/// component it is in <c>data-solo-kit</c>, and is the element a browser
/// already knows how to press, focus and read out.
/// </summary>
internal sealed class PlainKitRenderer : ISoloKitRenderer
{
    public static readonly PlainKitRenderer Instance = new();

    public void Render(RenderTreeBuilder builder, SoloKitComponent component)
    {
        switch (component)
        {
            case SoloStack stack:
                builder.OpenElement(0, "div");
                builder.AddAttribute(1, "data-solo-kit", "stack");
                builder.AddAttribute(2, "style",
                    $"display:flex;flex-direction:{(stack.Direction == SoloDirection.Horizontal ? "row" : "column")};" +
                    $"gap:{Gap(stack.Spacing)}px;align-items:{Align(stack.Align)}");
                builder.AddContent(3, stack.ChildContent);
                builder.CloseElement();
                break;

            case SoloText text:
                builder.OpenElement(10, "span");
                builder.AddAttribute(11, "data-solo-kit", "text");
                builder.AddAttribute(12, "data-variant", text.Variant.ToString());
                builder.AddAttribute(13, "data-tone", text.Tone.ToString());
                builder.AddContent(14, text.Text);
                builder.CloseElement();
                break;

            case SoloIcon icon:
                builder.OpenElement(20, "span");
                builder.AddAttribute(21, "data-solo-kit", "icon");
                builder.AddAttribute(22, "data-icon", icon.Name);
                builder.AddAttribute(23, "data-tone", icon.Tone.ToString());
                if (icon.Label is null)
                {
                    builder.AddAttribute(24, "aria-hidden", "true");
                }
                else
                {
                    builder.AddAttribute(25, "role", "img");
                    builder.AddAttribute(26, "aria-label", icon.Label);
                }

                builder.CloseElement();
                break;

            case SoloBadge badge:
                builder.OpenElement(30, "span");
                builder.AddAttribute(31, "data-solo-kit", "badge");
                builder.AddAttribute(32, "data-icon", badge.Icon);
                builder.AddAttribute(33, "data-tone", badge.Tone.ToString());
                builder.AddContent(34, badge.Text);
                builder.CloseElement();
                break;

            case SoloButton button when button.Href is not null && !button.Disabled:
                builder.OpenElement(40, "a");
                builder.AddAttribute(41, "data-solo-kit", "button");
                builder.AddAttribute(42, "href", button.Href);
                builder.AddAttribute(43, "onclick", button.OnClick);
                builder.AddContent(44, button.Text);
                builder.CloseElement();
                break;

            case SoloButton button:
                builder.OpenElement(50, "button");
                builder.AddAttribute(51, "data-solo-kit", "button");
                builder.AddAttribute(52, "type", "button");
                builder.AddAttribute(53, "disabled", button.Disabled);
                builder.AddAttribute(54, "onclick", button.OnClick);
                builder.AddContent(55, button.Text);
                builder.CloseElement();
                break;

            case SoloRow row:
                var goes = row.Href is not null && !row.Disabled;
                var acts = !row.Disabled && (goes || row.OnClick.HasDelegate);
                builder.OpenElement(60, goes ? "a" : "div");
                builder.AddAttribute(61, "data-solo-kit", "row");
                builder.AddAttribute(62, "data-icon", row.Icon);
                if (goes)
                {
                    builder.AddAttribute(63, "href", row.Href);
                }
                else if (acts)
                {
                    builder.AddAttribute(64, "role", "button");
                    builder.AddAttribute(65, "tabindex", "0");
                }

                if (acts)
                {
                    builder.AddAttribute(66, "onclick", row.OnClick);
                }

                builder.OpenElement(67, "span");
                builder.AddAttribute(68, "data-part", "title");
                builder.AddContent(69, row.Title);
                builder.CloseElement();
                if (row.Subtitle is not null)
                {
                    builder.OpenElement(70, "span");
                    builder.AddAttribute(71, "data-part", "subtitle");
                    builder.AddContent(72, row.Subtitle);
                    builder.CloseElement();
                }

                builder.AddContent(73, row.ChildContent);
                builder.CloseElement();
                break;

            case SoloProgress progress:
                builder.OpenElement(80, "progress");
                builder.AddAttribute(81, "data-solo-kit", "progress");
                builder.AddAttribute(82, "max", "1");
                builder.AddAttribute(83, "value", Math.Clamp(progress.Value, 0, 1).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
                builder.AddAttribute(84, "aria-label", progress.Label);
                builder.CloseElement();
                break;

            case SoloToggle toggle:
                builder.OpenElement(90, "label");
                builder.AddAttribute(91, "data-solo-kit", "toggle");
                builder.OpenElement(92, "input");
                builder.AddAttribute(93, "type", "checkbox");
                builder.AddAttribute(94, "checked", toggle.Value);
                builder.AddAttribute(95, "disabled", toggle.Disabled);
                builder.AddAttribute(96, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(
                    toggle, change => toggle.ValueChanged.InvokeAsync(change.Value is true)));
                builder.CloseElement();
                builder.AddContent(97, toggle.Label);
                builder.CloseElement();
                break;
        }
    }

    private static int Gap(SoloSpacing spacing) => spacing switch
    {
        SoloSpacing.None => 0,
        SoloSpacing.Small => 4,
        SoloSpacing.Medium => 8,
        _ => 16,
    };

    private static string Align(SoloAlign align) => align switch
    {
        SoloAlign.Center => "center",
        SoloAlign.End => "flex-end",
        SoloAlign.Stretch => "stretch",
        _ => "flex-start",
    };
}
