using Microsoft.EntityFrameworkCore;

namespace Solo.Mcp.Data;

/// <summary>
/// The extension's own store, in its own Postgres schema, with its own
/// migration history.
/// </summary>
/// <remarks>
/// <para>Everything the MCP feature persists lives here and nowhere else — no
/// entity is contributed to <c>SoloDbContext</c> and no migration is added to
/// core. Removing the package leaves this schema behind, attributable and
/// purgeable from Admin settings; core's own tables are untouched either way.</para>
///
/// <para>Tokens reference their owner by OIDC identity (issuer + subject)
/// rather than by core's user id: that pair is the stable contract core itself
/// keys users on, and it is exactly what the authentication handler replays to
/// let core resolve the user. No foreign key crosses into core's schema, which
/// is what keeps core free to change its own tables.</para>
/// </remarks>
public sealed class McpDbContext(DbContextOptions<McpDbContext> options) : DbContext(options)
{
    /// <summary>Must match the manifest's <c>schema</c>; <see cref="McpExtension"/> checks that it does.</summary>
    public const string Schema = "ext_mcp";

    public DbSet<McpToken> Tokens => Set<McpToken>();

    public DbSet<McpTokenUsage> Usage => Set<McpTokenUsage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<McpToken>(e =>
        {
            e.ToTable("Tokens");
            // SHA-256, hex-encoded: fixed 64 chars. Unique — the hash is the lookup key.
            e.Property(t => t.TokenHash).HasMaxLength(64);
            e.Property(t => t.Issuer).HasMaxLength(500);
            e.Property(t => t.Subject).HasMaxLength(500);
            e.Property(t => t.Email).HasMaxLength(320);
            e.Property(t => t.DisplayName).HasMaxLength(300);
            e.Property(t => t.Description).HasMaxLength(200);
            e.Property(t => t.RevokedReason).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(t => t.TokenHash).IsUnique();
            // The owner's page reads by identity; the admin audit reads everything.
            e.HasIndex(t => new { t.Issuer, t.Subject });
        });

        modelBuilder.Entity<McpTokenUsage>(e =>
        {
            e.ToTable("TokenUsage");
            // (token, hour) is the natural key and the upsert's conflict target.
            e.HasKey(u => new { u.TokenId, u.Hour });
            // The admin graph reads one window across every token.
            e.HasIndex(u => u.Hour);
            e.HasOne(u => u.Token).WithMany(t => t.Usage)
                .HasForeignKey(u => u.TokenId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}

/// <summary>
/// A per-user credential an MCP client presents to speak for its owner.
/// </summary>
/// <remarks>
/// <para>Only the SHA-256 of the token is stored, so nobody — including a super
/// admin — can read a token back out of the database. Regeneration revokes the
/// previous row rather than overwriting it, which is what keeps the admin audit
/// honest: every token an account has ever held stays listed with the times it
/// was created and cut off.</para>
///
/// <para>The token speaks for its owner against the whole REST API, so it is
/// exactly as privileged as they are. The one thing it deliberately cannot do is
/// manage credentials — mint a Solo session, or generate another MCP token — so
/// a leaked token cannot renew itself past the revocation meant to kill it.</para>
/// </remarks>
public sealed class McpToken
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>SHA-256 of the opaque bearer token, hex-encoded. The lookup key.</summary>
    public required string TokenHash { get; set; }

    /// <summary>OIDC issuer of the owner — half of core's own user identity.</summary>
    public required string Issuer { get; set; }

    /// <summary>OIDC subject of the owner — the other half.</summary>
    public required string Subject { get; set; }

    /// <summary>Owner's address at generation time. Replayed as a claim, and shown in the audit.</summary>
    public string? Email { get; set; }

    /// <summary>Owner's display name at generation time, for the admin audit.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Owner-supplied label, e.g. "Claude Code on the laptop".</summary>
    public string? Description { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Last request this token authenticated; null until it is first used.</summary>
    public DateTimeOffset? LastUsedAt { get; set; }

    /// <summary>Set once, when the token stops working. Null means live.</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    public McpTokenRevocation? RevokedReason { get; set; }

    public List<McpTokenUsage> Usage { get; set; } = [];
}

/// <summary>Why a token stopped working. Shown in the admin audit.</summary>
public enum McpTokenRevocation
{
    /// <summary>The owner generated a replacement, which cut this one off.</summary>
    Regenerated = 0,

    /// <summary>The owner revoked it deliberately.</summary>
    RevokedByOwner = 1,

    /// <summary>An admin revoked it, most likely through the emergency revoke-all.</summary>
    RevokedByAdmin = 2,
}

/// <summary>
/// One hour of request counts for one token — the series behind the admin's
/// 24-hour usage graph.
/// </summary>
/// <remarks>
/// Bucketed rather than one row per request: the graph only ever asks "how much,
/// per hour", so a row per request would be a write-amplified log nobody reads.
/// Authentication upserts the current hour on every request it accepts, which is
/// one statement and correct under concurrency.
/// </remarks>
public sealed class McpTokenUsage
{
    public Guid TokenId { get; set; }

    public McpToken Token { get; set; } = null!;

    /// <summary>The UTC hour this bucket counts, truncated to the hour.</summary>
    public DateTimeOffset Hour { get; set; }

    public int Count { get; set; }
}
