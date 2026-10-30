using System.Text.Json.Serialization;

namespace Solo.Api.Updates;

/// <summary>
/// The <c>release-manifest.json</c> a Solo release carries, as much of it as
/// the server reads. See <c>docs/releases.md</c> for the file itself and
/// <c>build/release-manifest.mjs</c> for what writes it.
/// </summary>
/// <remarks>
/// Deliberately tolerant. Every member is optional and unknown members are
/// ignored, because this type parses a document produced by a <em>newer</em>
/// release than the one running it — that is the whole point of an update
/// check — and a release that adds a field must not make older instances stop
/// seeing updates. <see cref="Schema"/> is the one thing worth refusing on, and
/// only when it moves past what this code understands.
/// </remarks>
public sealed class ReleaseFeed
{
    /// <summary>Contract version of the file. 1 is the only one that has existed.</summary>
    public int Schema { get; set; }

    public ReleaseFeedRelease? Release { get; set; }

    public ReleaseFeedServer? Server { get; set; }

    public List<ReleaseFeedExtension> Extensions { get; set; } = [];

    /// <summary>The highest <see cref="Schema"/> this build knows how to read.</summary>
    public const int SupportedSchema = 1;
}

public sealed class ReleaseFeedRelease
{
    public string? Tag { get; set; }

    public string? Version { get; set; }

    public bool Prerelease { get; set; }

    public DateTimeOffset? Published { get; set; }
}

public sealed class ReleaseFeedServer
{
    public string? Version { get; set; }

    /// <summary>Tag of the release whose assets this entry points at.</summary>
    public string? Release { get; set; }

    /// <summary>Fully qualified image reference an operator pulls to update.</summary>
    public string? Image { get; set; }

    /// <summary>Extension contract the release implements.</summary>
    public string? Contract { get; set; }
}

public sealed class ReleaseFeedExtension
{
    public string? Id { get; set; }

    public string? Version { get; set; }

    public string? Release { get; set; }

    public string? Asset { get; set; }

    public string? Url { get; set; }

    public long Size { get; set; }

    /// <summary>Base64 SHA-512 of the asset, checked before anything is installed.</summary>
    public string? Sha512 { get; set; }

    [JsonPropertyName("soloApiVersion")]
    public string? SoloApiVersion { get; set; }

    [JsonPropertyName("soloClientVersion")]
    public string? SoloClientVersion { get; set; }
}
