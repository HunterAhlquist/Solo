using Solo.Data.Domain;
using Solo.Data.Entities;
using Solo.Shared;
using Xunit;

namespace Solo.Data.Tests;

public class LabelScopeTests
{
    private static readonly Guid ProjectId = Guid.NewGuid();
    private static readonly Guid BoardA = Guid.NewGuid();
    private static readonly Guid BoardB = Guid.NewGuid();

    private static readonly List<Label> Labels =
    [
        new() { ProjectId = ProjectId, BoardId = null, Kind = LabelKind.Task, Name = "Bug", Color = "#e5484d" },
        new() { ProjectId = ProjectId, BoardId = BoardA, Kind = LabelKind.Task, Name = "Board-A only", Color = "#111111" },
        new() { ProjectId = ProjectId, BoardId = null, Kind = LabelKind.SubTask, Name = "Code", Color = "#8e4ec6" },
    ];

    [Fact]
    public void GlobalLabels_VisibleOnEveryBoard()
    {
        Assert.Contains(LabelScope.ResolveForBoard(Labels, BoardA), l => l.Name == "Bug");
        Assert.Contains(LabelScope.ResolveForBoard(Labels, BoardB), l => l.Name == "Bug");
    }

    [Fact]
    public void BoardLocalLabels_VisibleOnlyOnTheirBoard()
    {
        Assert.Contains(LabelScope.ResolveForBoard(Labels, BoardA), l => l.Name == "Board-A only");
        Assert.DoesNotContain(LabelScope.ResolveForBoard(Labels, BoardB), l => l.Name == "Board-A only");
    }

    [Fact]
    public void KindFilter_SeparatesTaskAndSubTaskLabels()
    {
        var taskLabels = LabelScope.ResolveForBoard(Labels, BoardA, LabelKind.Task).ToList();
        Assert.All(taskLabels, l => Assert.Equal(LabelKind.Task, l.Kind));
        Assert.DoesNotContain(taskLabels, l => l.Name == "Code");
    }

    [Fact]
    public void Assignability_RequiresMatchingKindAndScope()
    {
        var boardLocal = Labels.Single(l => l.Name == "Board-A only");
        Assert.True(LabelScope.IsAssignable(boardLocal, BoardA, LabelKind.Task));
        Assert.False(LabelScope.IsAssignable(boardLocal, BoardB, LabelKind.Task));
        Assert.False(LabelScope.IsAssignable(boardLocal, BoardA, LabelKind.SubTask));
    }

    [Fact]
    public void Defaults_SeedFourTaskAndFourSubTaskLabels()
    {
        var defaults = LabelDefaults.ForProject(ProjectId);
        Assert.Equal(
            ["Bug", "Feature", "Improvement", "Feedback Session"],
            defaults.Where(l => l.Kind == LabelKind.Task).Select(l => l.Name));
        Assert.Equal(
            ["Code", "Asset", "Design", "Writing"],
            defaults.Where(l => l.Kind == LabelKind.SubTask).Select(l => l.Name));
        Assert.All(defaults, l => Assert.Null(l.BoardId));
    }
}
