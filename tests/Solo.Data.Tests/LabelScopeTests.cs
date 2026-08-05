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

public class BoardLabelsTests
{
    private static readonly Guid ProjectId = Guid.NewGuid();
    private static readonly Guid BoardId = Guid.NewGuid();

    private static List<Label> Defaults() => LabelDefaults.ForProject(ProjectId);

    [Fact]
    public void CopyToBoard_ClonesEveryDefaultOntoTheBoard()
    {
        var defaults = Defaults();

        var copies = BoardLabels.CopyToBoard(defaults, BoardId);

        Assert.Equal(defaults.Select(l => l.Name), copies.Select(l => l.Name));
        Assert.All(copies, l => Assert.Equal(BoardId, l.BoardId));
        Assert.All(copies, l => Assert.Equal(ProjectId, l.ProjectId));
        Assert.Equal(defaults.Select(l => (Guid?)l.Id), copies.Select(l => l.SourceId));
        Assert.Empty(defaults.Select(l => l.Id).Intersect(copies.Select(l => l.Id)));
    }

    /// <summary>
    /// The rename is the case that matters: the copy is tracked to the default it
    /// came from, so it is restored rather than deleted and re-created, and the
    /// items pointing at it keep their label.
    /// </summary>
    [Fact]
    public void Reset_RestoresARenamedCopyInPlaceSoItsItemsKeepIt()
    {
        var defaults = Defaults();
        var current = BoardLabels.CopyToBoard(defaults, BoardId);
        var bug = current.Single(l => l.Name == "Bug");
        var id = bug.Id;
        bug.Name = "Defect";
        bug.Color = "#010203";
        bug.Icon = "wrench";

        var result = BoardLabels.ResetToDefaults(defaults, current, BoardId);

        Assert.Empty(result.Added);
        Assert.Empty(result.Removed);
        Assert.Equal(id, bug.Id);
        Assert.Equal("Bug", bug.Name);
        Assert.Equal("#e5484d", bug.Color);
        Assert.Null(bug.Icon);
    }

    /// <summary>Labels predating origin tracking still match on kind and name.</summary>
    [Fact]
    public void Reset_FallsBackToNameMatchingForUntrackedLabels()
    {
        var defaults = Defaults();
        var current = BoardLabels.CopyToBoard(defaults, BoardId);
        var bug = current.Single(l => l.Name == "Bug");
        bug.SourceId = null;
        bug.Color = "#010203";

        var result = BoardLabels.ResetToDefaults(defaults, current, BoardId);

        Assert.Empty(result.Added);
        Assert.Empty(result.Removed);
        Assert.Equal("#e5484d", bug.Color);
        Assert.Equal(defaults.Single(l => l.Name == "Bug").Id, bug.SourceId);
    }

    [Fact]
    public void Reset_ReaddsAMissingDefaultAndDropsALabelTheDefaultsDontName()
    {
        var defaults = Defaults();
        var current = BoardLabels.CopyToBoard(defaults, BoardId);
        current.RemoveAll(l => l.Name == "Feature");
        var invented = new Label
        {
            ProjectId = ProjectId, BoardId = BoardId, Kind = LabelKind.Task,
            Name = "Playtest", Color = "#123456",
        };
        current.Add(invented);

        var result = BoardLabels.ResetToDefaults(defaults, current, BoardId);

        Assert.Equal(["Feature"], result.Added.Select(l => l.Name));
        Assert.All(result.Added, l => Assert.Equal(BoardId, l.BoardId));
        Assert.Equal([invented], result.Removed);
    }

    // -- Counterpart: what a label becomes when its item moves boards ------------

    private static readonly Guid OtherBoardId = Guid.NewGuid();

    /// <summary>
    /// Two boards stamped from the same defaults hold different rows for the same
    /// label, so a task moving between them has to be re-pointed — including when
    /// the destination has since renamed its copy.
    /// </summary>
    [Fact]
    public void Counterpart_FollowsTheSharedDefaultEvenAcrossARename()
    {
        var defaults = Defaults();
        var here = BoardLabels.CopyToBoard(defaults, BoardId).Single(l => l.Name == "Bug");
        var there = BoardLabels.CopyToBoard(defaults, OtherBoardId);
        there.Single(l => l.Name == "Bug").Name = "Defect";

        var match = BoardLabels.Counterpart(here, there);

        Assert.NotNull(match);
        Assert.Equal("Defect", match.Name);
        Assert.Equal(here.SourceId, match.SourceId);
        Assert.NotEqual(here.Id, match.Id);
    }

    /// <summary>A label a board invented has no shared origin, so name is all there is.</summary>
    [Fact]
    public void Counterpart_FallsBackToKindAndNameIgnoringCase()
    {
        var invented = new Label
        {
            ProjectId = ProjectId, BoardId = BoardId, Kind = LabelKind.Task,
            Name = "Playtest blocker", Color = "#123456",
        };
        var there = new List<Label>
        {
            new()
            {
                ProjectId = ProjectId, BoardId = OtherBoardId, Kind = LabelKind.SubTask,
                Name = "playtest blocker", Color = "#654321",
            },
            new()
            {
                ProjectId = ProjectId, BoardId = OtherBoardId, Kind = LabelKind.Task,
                Name = "PLAYTEST BLOCKER", Color = "#654321",
            },
        };

        var match = BoardLabels.Counterpart(invented, there);

        Assert.NotNull(match);
        Assert.Equal(LabelKind.Task, match.Kind); // the same-name sub-task label is a different label
    }

    [Fact]
    public void Counterpart_IsNullWhenTheOtherBoardHasNothingLikeIt()
    {
        var invented = new Label
        {
            ProjectId = ProjectId, BoardId = BoardId, Kind = LabelKind.Task,
            Name = "Playtest blocker", Color = "#123456",
        };

        Assert.Null(BoardLabels.Counterpart(invented, BoardLabels.CopyToBoard(Defaults(), OtherBoardId)));
    }

    /// <summary>Kind is part of a label's identity: same name, different kind is a different label.</summary>
    [Fact]
    public void Reset_MatchesOnKindAsWellAsName()
    {
        var defaults = Defaults();
        var current = new List<Label>
        {
            new()
            {
                ProjectId = ProjectId, BoardId = BoardId, Kind = LabelKind.SubTask,
                Name = "Bug", Color = "#ffffff",
            },
        };

        var result = BoardLabels.ResetToDefaults(defaults, current, BoardId);

        Assert.Contains(result.Added, l => l.Kind == LabelKind.Task && l.Name == "Bug");
        Assert.Equal(current, result.Removed);
    }
}
