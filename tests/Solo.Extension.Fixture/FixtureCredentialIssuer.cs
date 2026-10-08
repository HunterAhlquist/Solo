using Solo.Extensibility;

namespace Solo.Extension.Fixture;

/// <summary>
/// Revokes nothing, except that it fails for one subject — so a test can show
/// that core reports an extension that couldn't revoke its credentials rather
/// than telling the admin everything is gone.
/// </summary>
public sealed class FixtureCredentialIssuer : ISoloCredentialIssuer
{
    public const string UnrevocableSubject = "unrevocable";

    public Task<int> RevokeAllAsync(CredentialHolder holder, CancellationToken ct) =>
        holder.Subject == UnrevocableSubject
            ? throw new InvalidOperationException("The fixture's credential store is unavailable.")
            : Task.FromResult(0);
}
