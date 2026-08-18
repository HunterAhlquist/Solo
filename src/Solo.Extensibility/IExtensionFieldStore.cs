namespace Solo.Extensibility;

/// <summary>
/// Per-extension custom fields on core entities, stored in a jsonb column keyed
/// by extension id.
/// </summary>
/// <remarks>
/// <para>This is how an extension annotates something that already exists —
/// a field on a task — without a schema change. One core migration added the
/// column; nothing changes per extension after that.</para>
///
/// <para>An extension can only read and write its own key: the id comes from
/// the host, not from the caller. Note the audit consequence — the interceptor
/// records the whole bag as one column change, not a per-key diff.</para>
///
/// <para>These live in CORE tables, so a purge that only drops the extension's
/// schema leaves them behind. See <c>ExtensionRegistry.PurgeAsync</c>.</para>
/// </remarks>
public interface IExtensionFieldStore
{
    Task<T?> GetAsync<T>(ExtensionEntity entity, Guid entityId, CancellationToken ct = default)
        where T : class;

    Task SetAsync<T>(ExtensionEntity entity, Guid entityId, T value, CancellationToken ct = default)
        where T : class;

    Task ClearAsync(ExtensionEntity entity, Guid entityId, CancellationToken ct = default);
}

/// <summary>Core entities carrying an extension field bag.</summary>
public enum ExtensionEntity
{
    Task = 0,
    Epic = 1,
    Board = 2,
}
