namespace Solo.Extensibility;

/// <summary>
/// An extension that issues credentials of its own on a user's behalf — tokens,
/// keys — and can revoke them. When a super admin signs a user out everywhere,
/// core revokes its own sessions and then calls every enabled issuer, so one
/// action cuts off everything the user holds.
/// </summary>
/// <remarks>
/// <para>Called in the request and awaited, unlike an <see cref="ISoloEventHandler"/>:
/// an admin told that someone's access is gone must be able to rely on it. A
/// failure reaches the admin with the extension named, and core's sessions stay
/// revoked either way.</para>
///
/// <para>Register with
/// <see cref="ExtensionServiceCollectionExtensions.AddCredentialIssuer{TIssuer}"/>.
/// A disabled extension is not called: its tables may not exist.</para>
/// </remarks>
public interface ISoloCredentialIssuer
{
    /// <summary>Revokes every live credential this extension issued to the holder, returning how many.</summary>
    Task<int> RevokeAllAsync(CredentialHolder holder, CancellationToken ct);
}

/// <summary>
/// Whose credentials to revoke: the user's id, and the provider issuer and
/// subject that core keys them on — which is what an extension storing
/// credentials against the OIDC identity matches.
/// </summary>
public sealed record CredentialHolder(Guid UserId, string Issuer, string Subject);
