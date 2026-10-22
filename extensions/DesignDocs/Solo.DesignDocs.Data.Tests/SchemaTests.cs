using Microsoft.EntityFrameworkCore;
using Solo.DesignDocs.Data;

namespace Solo.DesignDocs.Data.Tests;

[Collection("postgres")]
public sealed class SchemaTests(PostgresFixture postgres)
{
    private async Task<DesignDocsDbContext> Database()
    {
        postgres.SkipUnlessDockerAvailable();
        var db = PostgresFixture.Context(await postgres.CreateDatabaseAsync());
        await db.Database.MigrateAsync();
        return db;
    }

    [SkippableTheory]
    [InlineData("vault")][InlineData("folder")][InlineData("note")]
    [InlineData("sketch")][InlineData("image")][InlineData("color")]
    public async Task Names_and_color_slots_are_unique_in_their_scope(string kind)
    {
        await using var db = await Database();
        var vault = new Vault { ProjectId = Guid.NewGuid(), Name = "Design" };
        var note = new Note { VaultId = vault.Id, Name = "Note" };
        db.AddRange(vault, note); await db.SaveChangesAsync();
        object Row() => kind switch {
            "vault" => new Vault { ProjectId = vault.ProjectId, Name = "Duplicate" },
            "folder" => new Folder { VaultId = vault.Id, Name = "Duplicate" },
            "note" => new Note { VaultId = vault.Id, Name = "Duplicate" },
            "sketch" => new Sketch { VaultId = vault.Id, NoteId = note.Id, Name = "Duplicate" },
            "image" => new DocImage { VaultId = vault.Id, Path = "attachments/image.webp" },
            _ => new MemberColor { ProjectId = vault.ProjectId, UserId = Guid.NewGuid(), Slot = 0 },
        };
        db.Add(Row()); await db.SaveChangesAsync(); db.Add(Row());
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [SkippableFact]
    public async Task Cross_vault_parent_and_note_references_are_rejected()
    {
        await using var db = await Database();
        var a = new Vault { Name = "A" }; var b = new Vault { Name = "B" };
        var folder = new Folder { VaultId = a.Id, Name = "Folder" };
        db.AddRange(a, b, folder); await db.SaveChangesAsync();
        db.Add(new Note { VaultId = b.Id, FolderId = folder.Id, Name = "Invalid" });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [SkippableTheory]
    [InlineData(false)][InlineData(true)]
    public async Task Deleting_note_or_vault_cascades_sketches_and_both_logs(bool deleteVault)
    {
        await using var db = await Database();
        var vault = new Vault { Name = "Design" };
        var folder = new Folder { VaultId = vault.Id, Name = "Folder" };
        var note = new Note { VaultId = vault.Id, FolderId = folder.Id, Name = "Note" };
        var sketch = new Sketch { VaultId = vault.Id, NoteId = note.Id, Name = "Sketch" };
        db.AddRange(vault, folder, note, sketch, new DocImage { VaultId = vault.Id, Path = "image.webp" },
            new DocUpdate { DocumentId = note.Id, NoteId = note.Id, Data = [1,2] },
            new DocUpdate { DocumentId = sketch.Id, SketchId = sketch.Id, Data = [3,4] });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        if (deleteVault) await db.Vaults.Where(v => v.Id == vault.Id).ExecuteDeleteAsync();
        else await db.Notes.Where(n => n.Id == note.Id).ExecuteDeleteAsync();
        Assert.Empty(await db.Notes.ToListAsync()); Assert.Empty(await db.Sketches.ToListAsync());
        Assert.Empty(await db.DocUpdates.ToListAsync());
        Assert.Equal(deleteVault ? 0 : 1, await db.Images.CountAsync());
        Assert.Equal(deleteVault ? 0 : 1, await db.Folders.CountAsync());
    }

    [SkippableFact]
    public async Task Log_requires_exactly_one_matching_document_owner()
    {
        await using var db = await Database();
        db.Add(new DocUpdate { DocumentId = Guid.NewGuid(), Data = [1] });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
