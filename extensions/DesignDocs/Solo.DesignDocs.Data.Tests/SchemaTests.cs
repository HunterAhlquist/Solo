using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Solo.DesignDocs.Data.Tests;

/// <summary>The schema's own rules: names unique where Obsidian needs them to be, and deletes that cascade.</summary>
[Collection("postgres")]
public sealed class SchemaTests(PostgresFixture postgres)
{
    private static readonly Guid Project = Guid.NewGuid();

    [SkippableFact]
    public async Task Vault_names_are_unique_per_project_only()
    {
        postgres.SkipUnlessDockerAvailable();
        await using var db = PostgresFixture.Context(await postgres.CreateDatabaseAsync());
        db.Vaults.Add(new Vault { ProjectId = Project, Name = "Combat" });
        db.Vaults.Add(new Vault { ProjectId = Guid.NewGuid(), Name = "Combat" });
        await db.SaveChangesAsync();

        db.Vaults.Add(new Vault { ProjectId = Project, Name = "Combat" });
        await AssertUniqueViolationAsync(db);
    }

    /// <summary>Two notes with one name at the vault's root clash, though their folder is null.</summary>
    [SkippableFact]
    public async Task Root_names_clash_even_though_their_parent_is_null()
    {
        postgres.SkipUnlessDockerAvailable();
        await using var db = PostgresFixture.Context(await postgres.CreateDatabaseAsync());
        var vault = new Vault { ProjectId = Project, Name = "Docs" };
        db.Vaults.Add(vault);
        db.Notes.Add(new Note { VaultId = vault.Id, Name = "Readme" });
        db.Folders.Add(new Folder { VaultId = vault.Id, Name = "Art" });
        await db.SaveChangesAsync();

        db.Notes.Add(new Note { VaultId = vault.Id, Name = "Readme" });
        await AssertUniqueViolationAsync(db);

        db.Folders.Add(new Folder { VaultId = vault.Id, Name = "Art" });
        await AssertUniqueViolationAsync(db);
    }

    [SkippableFact]
    public async Task The_same_name_in_different_folders_is_fine()
    {
        postgres.SkipUnlessDockerAvailable();
        await using var db = PostgresFixture.Context(await postgres.CreateDatabaseAsync());
        var vault = new Vault { ProjectId = Project, Name = "Docs" };
        var art = new Folder { VaultId = vault.Id, Name = "Art" };
        var code = new Folder { VaultId = vault.Id, Name = "Code" };
        db.AddRange(vault, art, code);
        db.Notes.AddRange(
            new Note { VaultId = vault.Id, FolderId = art.Id, Name = "Readme" },
            new Note { VaultId = vault.Id, FolderId = code.Id, Name = "Readme" },
            new Note { VaultId = vault.Id, Name = "Readme" });
        await db.SaveChangesAsync();

        Assert.Equal(3, await db.Notes.CountAsync());
    }

    [SkippableFact]
    public async Task Sketch_and_image_names_are_unique_per_vault()
    {
        postgres.SkipUnlessDockerAvailable();
        await using var db = PostgresFixture.Context(await postgres.CreateDatabaseAsync());
        var vault = new Vault { ProjectId = Project, Name = "Docs" };
        var first = new Note { VaultId = vault.Id, Name = "One" };
        var second = new Note { VaultId = vault.Id, Name = "Two" };
        db.AddRange(vault, first, second);
        db.Sketches.Add(new Sketch { VaultId = vault.Id, NoteId = first.Id, Name = "Map" });
        db.Images.Add(new VaultImage { VaultId = vault.Id, Path = "attachments/a.webp", ContentType = "image/webp", Data = [1] });
        await db.SaveChangesAsync();

        // Embeds name a sketch alone, so another note can't reuse the name.
        db.Sketches.Add(new Sketch { VaultId = vault.Id, NoteId = second.Id, Name = "Map" });
        await AssertUniqueViolationAsync(db);

        db.Images.Add(new VaultImage { VaultId = vault.Id, Path = "attachments/a.webp", ContentType = "image/webp", Data = [2] });
        await AssertUniqueViolationAsync(db);
    }

    [SkippableFact]
    public async Task Deleting_a_vault_takes_everything_in_it()
    {
        postgres.SkipUnlessDockerAvailable();
        var connectionString = await postgres.CreateDatabaseAsync();
        await using var db = PostgresFixture.Context(connectionString);
        var (vault, kept) = (new Vault { ProjectId = Project, Name = "Doomed" }, new Vault { ProjectId = Project, Name = "Kept" });
        var folder = new Folder { VaultId = vault.Id, Name = "F" };
        var note = new Note { VaultId = vault.Id, FolderId = folder.Id, Name = "N" };
        var keptNote = new Note { VaultId = kept.Id, Name = "N" };
        db.AddRange(vault, kept, folder, note, keptNote);
        db.Sketches.Add(new Sketch { VaultId = vault.Id, NoteId = note.Id, Name = "S" });
        db.Images.Add(new VaultImage { VaultId = vault.Id, Path = "i.png", ContentType = "image/png", Data = [1] });
        db.DocUpdates.Add(new DocUpdate { DocumentId = note.Id, VaultId = vault.Id, Data = [1] });
        db.DocUpdates.Add(new DocUpdate { DocumentId = keptNote.Id, VaultId = kept.Id, Data = [1] });
        await db.SaveChangesAsync();

        await db.Vaults.Where(v => v.Id == vault.Id).ExecuteDeleteAsync();

        Assert.Equal(0, await db.Folders.CountAsync());
        Assert.Equal([keptNote.Id], await db.Notes.Select(n => n.Id).ToListAsync());
        Assert.Equal(0, await db.Sketches.CountAsync());
        Assert.Equal(0, await db.Images.CountAsync());
        Assert.Equal([keptNote.Id], await db.DocUpdates.Select(u => u.DocumentId).ToListAsync());
    }

    [SkippableFact]
    public async Task Deleting_a_folder_takes_its_subfolders_notes_and_their_sketches()
    {
        postgres.SkipUnlessDockerAvailable();
        await using var db = PostgresFixture.Context(await postgres.CreateDatabaseAsync());
        var vault = new Vault { ProjectId = Project, Name = "Docs" };
        var top = new Folder { VaultId = vault.Id, Name = "Top" };
        var inner = new Folder { VaultId = vault.Id, ParentId = top.Id, Name = "Inner" };
        var note = new Note { VaultId = vault.Id, FolderId = inner.Id, Name = "Deep" };
        var outside = new Note { VaultId = vault.Id, Name = "Outside" };
        db.AddRange(vault, top, inner, note, outside);
        db.Sketches.Add(new Sketch { VaultId = vault.Id, NoteId = note.Id, Name = "S" });
        await db.SaveChangesAsync();

        await db.Folders.Where(f => f.Id == top.Id).ExecuteDeleteAsync();

        Assert.Equal(0, await db.Folders.CountAsync());
        Assert.Equal([outside.Id], await db.Notes.Select(n => n.Id).ToListAsync());
        Assert.Equal(0, await db.Sketches.CountAsync());
    }

    [SkippableFact]
    public async Task A_member_has_one_color_slot_per_project_and_slots_are_not_shared()
    {
        postgres.SkipUnlessDockerAvailable();
        await using var db = PostgresFixture.Context(await postgres.CreateDatabaseAsync());
        var user = Guid.NewGuid();
        db.MemberColors.Add(new MemberColor { ProjectId = Project, UserId = user, Slot = 0 });
        db.MemberColors.Add(new MemberColor { ProjectId = Guid.NewGuid(), UserId = user, Slot = 0 });
        await db.SaveChangesAsync();

        db.MemberColors.Add(new MemberColor { ProjectId = Project, UserId = Guid.NewGuid(), Slot = 0 });
        await AssertUniqueViolationAsync(db);
    }

    private static async Task AssertUniqueViolationAsync(DesignDocsDbContext db)
    {
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
        db.ChangeTracker.Clear();
    }
}
