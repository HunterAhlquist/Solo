namespace Solo.Shared;

/// <summary>
/// Serial-number aliases: every project and board carries a short handle
/// (A-Z0-9, max 8) that stands in for its GUID in routes and composes task
/// serial numbers. Uniqueness is per sibling set — project aliases across the
/// instance, board aliases within their project — enforced by unique indexes.
/// </summary>
public static class Aliases
{
    public const int MaxLength = 8;

    /// <summary>For [RegularExpression] on the create/update requests.</summary>
    public const string Pattern = "^[A-Z0-9]{1,8}$";

    public const string PatternError = "Use 1-8 characters: capital letters and digits only.";

    /// <summary>
    /// Route values arrive in whatever case the user typed; storage is upper.
    /// </summary>
    public static string Normalize(string alias) => alias.Trim().ToUpperInvariant();

    /// <summary>The user-facing serial number of a task, composed at runtime.</summary>
    public static string Serial(string projectAlias, string boardAlias, int number) =>
        $"{projectAlias}-{boardAlias}-{number}";
}
