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

public class EditingWarningTests : ComponentTestBase
{
    [Fact]
    public void Nobody_editing_means_no_warning()
    {
        var warning = Render<EditingWarning>(p => p.Add(c => c.Editors, []));

        Assert.Empty(warning.Markup.Trim());
    }

    [Fact]
    public void One_editor_is_named()
    {
        var warning = Render<EditingWarning>(p => p.Add(c => c.Editors,
        [
            new PresenceEntry(TestData.OtherId, "Hunter", EntityTypes.Task, Guid.NewGuid(), PresenceMode.Editing),
        ]));

        Assert.Contains("Hunter is editing this right now", warning.Markup);
    }

    [Fact]
    public void Several_editors_collapse_to_a_count()
    {
        var taskId = Guid.NewGuid();
        var warning = Render<EditingWarning>(p => p.Add(c => c.Editors,
        [
            new PresenceEntry(Guid.NewGuid(), "Hunter", EntityTypes.Task, taskId, PresenceMode.Editing),
            new PresenceEntry(Guid.NewGuid(), "Ada", EntityTypes.Task, taskId, PresenceMode.Editing),
            new PresenceEntry(Guid.NewGuid(), "Grace", EntityTypes.Task, taskId, PresenceMode.Editing),
        ]));

        Assert.Contains("Hunter and 2 others", warning.Markup);
    }
}
