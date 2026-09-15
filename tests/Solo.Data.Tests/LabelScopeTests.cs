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
        new() { ProjectId = ProjectId, BoardId = null, Kind = LabelKind.Task, Name = "Default", Color = "#e5484d" },
        new() { ProjectId = ProjectId, BoardId = BoardA, Kind = LabelKind.Task, Name = "Bug", Color = "#e5484d" },
        new() { ProjectId = ProjectId, BoardId = BoardB, Kind = LabelKind.Task, Name = "Bug", Color = "#e5484d" },
        new() { ProjectId = ProjectId, BoardId = BoardA, Kind = LabelKind.SubTask, Name = "Code", Color = "#8e4ec6" },
    ];

    [Fact]
    public void ABoardSeesOnlyItsOwnLabels()
    {
        var onA = LabelScope.ResolveForBoard(Labels, BoardA).ToList();
        Assert.All(onA, l => Assert.Equal(BoardA, l.BoardId));
        Assert.Equal(["Bug", "Code"], onA.Select(l => l.Name));
    }

    /// <summary>Project defaults are a template for new boards, not labels a board carries.</summary>
    [Fact]
    public void ProjectDefaults_AreVisibleOnNoBoard()
    {
        Assert.DoesNotContain(LabelScope.ResolveForBoard(Labels, BoardA), l => l.Name == "Default");
        Assert.DoesNotContain(LabelScope.ResolveForBoard(Labels, BoardB), l => l.Name == "Default");
    }

    [Fact]
    public void KindFilter_SeparatesTaskAndSubTaskLabels()
    {
        var taskLabels = LabelScope.ResolveForBoard(Labels, BoardA, LabelKind.Task).ToList();
        Assert.All(taskLabels, l => Assert.Equal(LabelKind.Task, l.Kind));
        Assert.DoesNotContain(taskLabels, l => l.Name == "Code");
    }

    [Fact]
    public void Assignability_RequiresMatchingKindAndBoard()
    {
        var onBoardA = Labels.Single(l => l.BoardId == BoardA && l.Kind == LabelKind.Task);
        Assert.True(LabelScope.IsAssignable(onBoardA, BoardA, LabelKind.Task));
        Assert.False(LabelScope.IsAssignable(onBoardA, BoardB, LabelKind.Task));
        Assert.False(LabelScope.IsAssignable(onBoardA, BoardA, LabelKind.SubTask));

        var projectDefault = Labels.Single(l => l.BoardId is null);
        Assert.False(LabelScope.IsAssignable(projectDefault, BoardA, LabelKind.Task));
    }

    [Fact]
    public void Defaults_SeedThreeTaskAndFourSubTaskLabels()
    {
        var defaults = LabelDefaults.ForProject(ProjectId);
        Assert.Equal(
            ["Bug", "Feature", "Improvement"],
            defaults.Where(l => l.Kind == LabelKind.Task).Select(l => l.Name));
        Assert.Equal(
            ["Code", "Asset", "Design", "Writing"],
            defaults.Where(l => l.Kind == LabelKind.SubTask).Select(l => l.Name));
        Assert.All(defaults, l => Assert.Null(l.BoardId));
    }
}
