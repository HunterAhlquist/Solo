namespace Solo.Extensions;

public sealed class ExtensionOptions
{
    public const string Section = "Extensions";

    /// <summary>
    /// Directory scanned for <c>*.zip</c> extension packages — a mounted volume
    /// in a deployment, a sibling of the themes volume. Missing or empty means
    /// no extensions.
    /// </summary>
    public string? Directory { get; set; }

    /// <summary>
    /// Additional package directories loaded unzipped. For tests and for a
    /// development loop that would rather not re-zip on every build.
    /// </summary>
    public List<string> DevelopmentDirectories { get; set; } = [];

    /// <summary>How long a Before hook may take before it is treated as failed.</summary>
    public TimeSpan HookTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>How long an After hook or event handler may take before it is abandoned.</summary>
    public TimeSpan NotificationTimeout { get; set; } = TimeSpan.FromSeconds(10);
}
