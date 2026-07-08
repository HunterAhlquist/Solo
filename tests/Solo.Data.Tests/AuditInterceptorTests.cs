using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Solo.Data.Auditing;
using Solo.Data.Entities;
using Solo.Shared;
using Xunit;
using TaskState = Solo.Shared.TaskState;

namespace Solo.Data.Tests;

public sealed class AuditInterceptorTests : IDisposable
{
    private sealed class FixedUser : ICurrentUserProvider
    {
        public Guid? UserId { get; set; }
    }

    private readonly SqliteConnection _connection;
    private readonly FixedUser _currentUser = new() { UserId = Guid.NewGuid() };

    public AuditInterceptorTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var db = CreateContext();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private SoloDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<SoloDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(new AuditSaveChangesInterceptor(_currentUser))
            .Options;
        return new SoloDbContext(options);
    }

    private (Guid ProjectId, Guid BoardId, Guid TaskId) SeedHierarchy()
    {
        using var db = CreateContext();
        var project = new Project { Name = "Game" };
        var board = new Board { Project = project, Name = "Alpha" };
        var task = new TaskItem { Board = board, Name = "Initial name" };
        db.AddRange(project, board, task);
        db.SaveChanges();
        return (project.Id, board.Id, task.Id);
    }

    [Fact]
    public void Creation_WritesOneRowWithNullColumnAndPreviousValue()
    {
        var (projectId, _, taskId) = SeedHierarchy();

        using var db = CreateContext();
        var creation = db.AuditEntries.Single(a => a.TableName == "Tasks" && a.RowId == taskId.ToString());
        Assert.Null(creation.ColumnName);
        Assert.Null(creation.PreviousValue);
        Assert.Equal(_currentUser.UserId, creation.UserId);
        Assert.Equal(projectId, creation.ProjectId);
    }

    [Fact]
    public void Modification_WritesOneRowPerChangedColumn_WithPreviousValue()
    {
        var (projectId, _, taskId) = SeedHierarchy();

        using (var db = CreateContext())
        {
            var task = db.Tasks.Single(t => t.Id == taskId);
            task.Name = "Renamed";
            task.Status = TaskState.InProgress;
            db.SaveChanges();
        }

        using (var db = CreateContext())
        {
            var rows = db.AuditEntries
                .Where(a => a.TableName == "Tasks" && a.RowId == taskId.ToString() && a.ColumnName != null)
                .ToList();
            Assert.Equal(2, rows.Count);

            var nameRow = rows.Single(r => r.ColumnName == nameof(TaskItem.Name));
            Assert.Equal("Initial name", nameRow.PreviousValue);
            Assert.Equal(projectId, nameRow.ProjectId);

            var statusRow = rows.Single(r => r.ColumnName == nameof(TaskItem.Status));
            Assert.Equal(nameof(TaskState.Unstarted), statusRow.PreviousValue);
        }
    }

    [Fact]
    public void UnchangedSave_WritesNothing()
    {
        var (_, _, taskId) = SeedHierarchy();

        int before;
        using (var db = CreateContext())
        {
            before = db.AuditEntries.Count();
            var task = db.Tasks.Single(t => t.Id == taskId);
            task.Name = task.Name;
            db.SaveChanges();
        }

        using (var db = CreateContext())
        {
            Assert.Equal(before, db.AuditEntries.Count());
        }
    }

    [Fact]
    public void Deletion_WritesPerColumnRows_WithFinalValues()
    {
        var (projectId, _, taskId) = SeedHierarchy();

        using (var db = CreateContext())
        {
            db.Tasks.Remove(db.Tasks.Single(t => t.Id == taskId));
            db.SaveChanges();
        }

        using (var db = CreateContext())
        {
            var rows = db.AuditEntries
                .Where(a => a.TableName == "Tasks" && a.RowId == taskId.ToString() && a.ColumnName != null)
                .ToList();
            Assert.Contains(rows, r => r.ColumnName == nameof(TaskItem.Name) && r.PreviousValue == "Initial name");
            Assert.All(rows, r => Assert.Equal(projectId, r.ProjectId));
        }
    }

    [Fact]
    public void CascadeDelete_StillResolvesProjectId_ForChildRows()
    {
        var (projectId, boardId, taskId) = SeedHierarchy();

        using (var db = CreateContext())
        {
            var board = db.Boards
                .Include(b => b.Tasks).ThenInclude(t => t.SubTasks)
                .Include(b => b.Epics)
                .Single(b => b.Id == boardId);
            db.Boards.Remove(board);
            db.SaveChanges();
        }

        using (var db = CreateContext())
        {
            // The board row was deleted in the same save, so the task's project
            // scope must have come from the change tracker, not the database.
            var taskRows = db.AuditEntries
                .Where(a => a.TableName == "Tasks" && a.RowId == taskId.ToString() && a.ColumnName != null)
                .ToList();
            Assert.NotEmpty(taskRows);
            Assert.All(taskRows, r => Assert.Equal(projectId, r.ProjectId));
        }
    }

    [Fact]
    public void AuditEntriesThemselves_AreNotAudited()
    {
        SeedHierarchy();

        using var db = CreateContext();
        Assert.DoesNotContain(db.AuditEntries, a => a.TableName == "AuditEntries");
    }

    [Fact]
    public void CommentEditsAndDeletes_AreAuditedLikeAnyOtherSave()
    {
        var (_, _, taskId) = SeedHierarchy();
        Guid commentId;

        using (var db = CreateContext())
        {
            var author = new User { Issuer = "iss", Subject = "sub", Email = "a@b.c" };
            var comment = new Comment { Author = author, TaskId = taskId, Body = "first draft" };
            db.AddRange(author, comment);
            db.SaveChanges();
            commentId = comment.Id;

            comment.Body = "edited";
            db.SaveChanges();

            db.Comments.Remove(comment);
            db.SaveChanges();
        }

        using (var db = CreateContext())
        {
            var rows = db.AuditEntries
                .Where(a => a.TableName == "Comments" && a.RowId == commentId.ToString())
                .ToList();
            Assert.Contains(rows, r => r.ColumnName == null && r.PreviousValue == null);
            Assert.Contains(rows, r => r.ColumnName == nameof(Comment.Body) && r.PreviousValue == "first draft");
            Assert.Contains(rows, r => r.ColumnName == nameof(Comment.Body) && r.PreviousValue == "edited");
        }
    }
}
