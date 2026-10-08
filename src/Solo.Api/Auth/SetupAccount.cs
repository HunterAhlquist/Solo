using System.Security.Cryptography;
using System.Text;

namespace Solo.Api.Auth;

/// <summary>
/// The static first-run account (Setup:Username / Setup:Password), read once at
/// startup. It refuses to exist with a password anyone could guess: the compose
/// template's placeholder is public along with the repo, and until the wizard
/// hands off, a per-IP rate limit is all that stands in front of this pair. An
/// instance deployed without editing it would belong to whoever typed the
/// template's values into its wizard first, and the rate limit is no help when
/// the first guess is right.
/// </summary>
public sealed class SetupAccount
{
    /// <summary>
    /// Long enough that ten guesses a minute never get anywhere. Anything an
    /// operator generates (<c>openssl rand -base64 32</c> is 44) clears it; a
    /// word they made up usually doesn't.
    /// </summary>
    public const int MinimumPasswordLength = 20;

    /// <summary>How every placeholder in deploy/docker-compose.yml and the README starts.</summary>
    private const string PlaceholderPrefix = "CHANGE_ME";

    private static readonly string Remedy =
        $"Set it to a random value of at least {MinimumPasswordLength} characters "
        + "(openssl rand -base64 32 makes one) and restart.";

    private readonly string _username;
    private readonly string _password;

    public SetupAccount(IConfiguration config)
    {
        _username = config["Setup:Username"] ?? "";
        _password = config["Setup:Password"] ?? "";
        // Named the way an operator sets them: compose environment variables.
        DisabledReason =
            string.IsNullOrWhiteSpace(_username) ? "Setup__Username is not set. Set it and restart."
            : string.IsNullOrWhiteSpace(_password) ? $"Setup__Password is not set. {Remedy}"
            : _password.StartsWith(PlaceholderPrefix, StringComparison.OrdinalIgnoreCase)
                ? $"Setup__Password is still the template's {PlaceholderPrefix} placeholder. {Remedy}"
            : _password.Length < MinimumPasswordLength
                ? $"Setup__Password is shorter than {MinimumPasswordLength} characters. {Remedy}"
            : null;
    }

    /// <summary>
    /// Why the account can't be used, naming the setting to fix; null when it
    /// can. A disabled account accepts nothing, the right pair included.
    /// </summary>
    public string? DisabledReason { get; }

    public bool Accepts(string username, string password) =>
        DisabledReason is null
        && FixedTimeEquals(username, _username)
        && FixedTimeEquals(password, _password);

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
