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

/// <summary>Save-event semantics: blur/explicit save commits, keystrokes never do.</summary>
public class MarkdownEditorTests : ComponentTestBase
{
    [Fact]
    public void Typing_updates_the_character_counter_but_never_commits()
    {
        var saves = new List<string>();
        var editor = Render<MarkdownEditor>(p => p
            .Add(c => c.CanWrite, true)
            .Add(c => c.AlwaysEditing, true)
            .Add(c => c.MaxLength, 500)
            .Add(c => c.OnSave, body => saves.Add(body)));

        // Immediate="true" binds per keystroke — that feeds the counter, and
        // nothing else: the save callback must stay untouched.
        editor.Find("textarea").Input("half-typed thought");

        Assert.Empty(saves);
        Assert.Contains("18 / 500", editor.Markup);
    }

    [Fact]
    public void Updating_the_field_does_not_commit_until_an_explicit_save()
    {
        var saves = new List<string>();
        var editor = Render<MarkdownEditor>(p => p
            .Add(c => c.CanWrite, true)
            .Add(c => c.AlwaysEditing, true)
            .Add(c => c.OnSave, body => saves.Add(body)));

        editor.Find("textarea").Input("half-typed thought");

        Assert.Empty(saves);
    }

    [Fact]
    public void The_save_button_commits_the_draft()
    {
        var saves = new List<string>();
        var editor = Render<MarkdownEditor>(p => p
            .Add(c => c.CanWrite, true)
            .Add(c => c.AlwaysEditing, true)
            .Add(c => c.SaveText, "Save")
            .Add(c => c.OnSave, body => saves.Add(body)));

        editor.Find("textarea").Input("## Design notes");
        editor.FindAll("button").First(b => b.TextContent.Contains("Save")).Click();

        Assert.Equal("## Design notes", Assert.Single(saves));
    }

    [Fact]
    public void A_read_only_viewer_never_gets_an_editor()
    {
        var editor = Render<MarkdownEditor>(p => p
            .Add(c => c.CanWrite, false)
            .Add(c => c.Value, "Some description"));

        editor.Find(".markdown-read").Click();

        Assert.Empty(editor.FindAll("textarea"));
    }

    [Fact]
    public void Opening_and_closing_the_editor_reports_editing_presence()
    {
        var reports = new List<bool>();
        var editor = Render<MarkdownEditor>(p => p
            .Add(c => c.CanWrite, true)
            .Add(c => c.Value, "text")
            .Add(c => c.OnEditingChanged, editing => reports.Add(editing)));

        editor.Find(".markdown-read").Click();
        Assert.Equal([true], reports);

        editor.FindAll("button").First(b => b.TextContent.Contains("Cancel")).Click();
        Assert.Equal([true, false], reports);
    }
}
