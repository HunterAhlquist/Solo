using Solo.Client.State;
using Solo.Shared.Extensibility;

namespace Solo.Client.Tests;

public sealed class WorkspaceTests
{
    [Fact]
    public void Nested_workspaces_restore_options_and_allow_out_of_order_disposal()
    {
        var state = new WorkspaceState();
        var outer = state.Enter(new(FullWidth:true, FillHeight:true, CollapseNavigation:true));
        var inner = state.Enter(new(FullWidth:true));
        Assert.False(state.Options.FillHeight);
        outer.Dispose();
        Assert.True(state.IsActive);
        Assert.False(state.Options.FillHeight);
        inner.Dispose();
        Assert.False(state.IsActive);
        Assert.Equal(new WorkspaceOptions(), state.Options);
        inner.Dispose();
        Assert.False(state.IsActive);
    }
}
