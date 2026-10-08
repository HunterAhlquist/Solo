namespace Solo.Extensibility;

/// <summary>
/// Work an extension does on its own schedule — a daily digest, a recurring
/// task — as a user who said it may (contract 1.8).
/// </summary>
/// <remarks>
/// <para>Outside a request there is nobody to act as. <see cref="ISoloQuery"/>
/// scopes every read to the caller, so with no caller it returns nothing, and
/// writes go through core's REST API, which needs a credential. This is the
/// way to have both: <see cref="RunAsAsync"/> gives the work a query and an
/// HTTP client that act as one user.</para>
///
/// <para><b>Grants.</b> The extension may act only as users who granted it,
/// and a grant can only come from the person: <see cref="GrantAsync"/> takes no
/// user id and records the caller of the request it runs in. Core keeps the
/// grants, not the extension, because revoking them is core's job — signing a
/// user out everywhere deletes theirs along with their sessions, so background
/// work stops on the same click as everything else they hold. Purging the
/// extension deletes its grants too.</para>
///
/// <para><b>What the work can do</b> is what a request from that user could
/// do, checked the same way at the time it runs: a member who has since left a
/// project can no longer touch it. Never more: background work does not carry
/// super admin, so it can't reach Admin settings' endpoints, and
/// <see cref="ExtensionCaller.IsSuperAdmin"/> is false in its scope.</para>
///
/// <para>Inject with <c>[FromKeyedServices("your-id")]</c>, like
/// <see cref="IExtensionFieldStore"/>. It is a singleton, so a hosted service
/// can take it in its constructor.</para>
/// </remarks>
public interface ISoloBackground
{
    /// <summary>
    /// Lets this extension act as the signed-in caller from now on. Call it from
    /// an endpoint that person uses to turn the feature on — saving a recurring
    /// task, enabling a digest. Granting twice is harmless.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Outside a request, for an anonymous caller, or from background work: a
    /// grant has to come from the person it names.
    /// </exception>
    Task GrantAsync(CancellationToken ct = default);

    /// <summary>
    /// Withdraws a user's grant, for when the last thing that needed it is gone.
    /// True when there was one to withdraw.
    /// </summary>
    Task<bool> RevokeAsync(Guid userId, CancellationToken ct = default);

    /// <summary>True when the user has granted this extension, and the grant hasn't been revoked.</summary>
    Task<bool> IsGrantedAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Runs <paramref name="work"/> as a user, in a DI scope of its own. Returns
    /// false without running it when the user has no grant, no longer exists, or
    /// the extension is disabled — expected states for a scheduler to skip, not
    /// errors. An exception from <paramref name="work"/> is the caller's.
    /// </summary>
    Task<bool> RunAsAsync(
        Guid userId, Func<SoloBackgroundScope, CancellationToken, Task> work, CancellationToken ct = default);
}

/// <summary>What background work runs with, all of it acting as one user.</summary>
public sealed class SoloBackgroundScope
{
    /// <summary>Who the work runs as. <see cref="ExtensionCaller.IsSuperAdmin"/> is always false.</summary>
    public required ExtensionCaller Caller { get; init; }

    /// <summary>Core reads, scoped to <see cref="Caller"/> as they would be in their own request.</summary>
    public required ISoloQuery Query { get; init; }

    /// <summary>
    /// Core's REST API, authenticated as <see cref="Caller"/>, with the instance
    /// as its base address: <c>Api.PutAsJsonAsync($"api/tasks/{id}/status", …)</c>.
    /// </summary>
    /// <remarks>
    /// The way to write, as in a request: a change made through it passes core's
    /// authorization, every extension's hooks, the audit log and the live
    /// broadcast. Core reads and writes enums as strings, so serialize with a
    /// <c>JsonStringEnumConverter</c>. Each request carries a short-lived
    /// credential checked against the grant, so a revoked grant stops the very
    /// next call. Don't keep the client beyond the work.
    /// </remarks>
    public required HttpClient Api { get; init; }

    /// <summary>The scope's services: your own DbContext and anything else you registered.</summary>
    public required IServiceProvider Services { get; init; }
}

/// <summary>Claims core puts on a request made by background work.</summary>
public static class SoloBackgroundClaims
{
    /// <summary>
    /// The id of the extension whose background work made the request. Set by
    /// core from the grant the credential names, never from anything the
    /// extension sends, so an <see cref="ISoloAuditContributor"/> can trust it to
    /// attribute a change to "recurring tasks" rather than to the person alone.
    /// </summary>
    public const string ExtensionId = "solo:ext";
}
