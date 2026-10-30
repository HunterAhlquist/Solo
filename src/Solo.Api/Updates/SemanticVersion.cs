using System.Globalization;

namespace Solo.Api.Updates;

/// <summary>
/// A SemVer 2.0 version, ordered the way the spec orders them. Enough of the
/// spec to answer one question: is the release in the feed newer than what is
/// running?
/// </summary>
/// <remarks>
/// <para><see cref="Version"/> cannot answer that. It has no concept of a
/// prerelease, so it reads <c>1.18.0-rc.1</c> as unparseable and
/// <c>1.16.6+c194865…</c> — which is exactly what <c>/api/config/version</c>
/// reports, because CI stamps the commit as build metadata — as unparseable
/// too. Comparing those as strings is worse: <c>"1.9.0" &gt; "1.18.0"</c>.</para>
///
/// <para>The two rules that are easy to get wrong, and that the tests pin:
/// build metadata is <em>ignored</em> in comparisons (<c>1.16.6+abc</c> and
/// <c>1.16.6+def</c> are the same version, which is why a rebuild of the same
/// release must not read as an update), and a prerelease sorts
/// <em>below</em> the release it leads to (<c>1.18.0-rc.1 &lt; 1.18.0</c>), so
/// a preview build is correctly told that the stable release supersedes
/// it.</para>
/// </remarks>
public sealed record SemanticVersion(
    int Major, int Minor, int Patch, string? Prerelease) : IComparable<SemanticVersion>
{
    public static bool TryParse(string? text, out SemanticVersion version)
    {
        version = null!;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var span = text.Trim();

        // A leading "v" is how the same number is written as a tag. Accepting it
        // means a feed may name either and nothing silently fails to match.
        if (span.Length > 0 && (span[0] == 'v' || span[0] == 'V'))
        {
            span = span[1..];
        }

        // Build metadata is dropped before anything else: it takes no part in
        // precedence, and it is the part most likely to be present.
        var plus = span.IndexOf('+');
        if (plus >= 0)
        {
            span = span[..plus];
        }

        string? prerelease = null;
        var dash = span.IndexOf('-');
        if (dash >= 0)
        {
            prerelease = span[(dash + 1)..];
            span = span[..dash];
            if (prerelease.Length == 0)
            {
                return false;
            }
        }

        var parts = span.Split('.');
        if (parts.Length is < 1 or > 3)
        {
            return false;
        }

        var numbers = new int[3];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return false;
            }
        }

        version = new SemanticVersion(numbers[0], numbers[1], numbers[2], prerelease);
        return true;
    }

    public int CompareTo(SemanticVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var core = Major.CompareTo(other.Major);
        if (core != 0) return core;
        core = Minor.CompareTo(other.Minor);
        if (core != 0) return core;
        core = Patch.CompareTo(other.Patch);
        if (core != 0) return core;

        // Equal cores: having a prerelease is what makes a version lower.
        if (Prerelease is null && other.Prerelease is null) return 0;
        if (Prerelease is null) return 1;
        if (other.Prerelease is null) return -1;

        return ComparePrerelease(Prerelease, other.Prerelease);
    }

    /// <summary>
    /// Dot-separated identifiers, left to right. Numeric ones compare
    /// numerically (so <c>rc.10</c> is above <c>rc.2</c>, which a string compare
    /// gets backwards), numeric sorts below alphanumeric, and a run-out of
    /// identifiers sorts below a longer set with the same prefix.
    /// </summary>
    private static int ComparePrerelease(string left, string right)
    {
        var a = left.Split('.');
        var b = right.Split('.');

        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var aNumeric = int.TryParse(a[i], NumberStyles.None, CultureInfo.InvariantCulture, out var an);
            var bNumeric = int.TryParse(b[i], NumberStyles.None, CultureInfo.InvariantCulture, out var bn);

            int result;
            if (aNumeric && bNumeric)
            {
                result = an.CompareTo(bn);
            }
            else if (aNumeric != bNumeric)
            {
                result = aNumeric ? -1 : 1;
            }
            else
            {
                result = string.CompareOrdinal(a[i], b[i]);
            }

            if (result != 0)
            {
                return result < 0 ? -1 : 1;
            }
        }

        return a.Length.CompareTo(b.Length);
    }

    /// <summary>True when <paramref name="candidate"/> supersedes <paramref name="running"/>.</summary>
    public static bool IsNewer(string? candidate, string? running) =>
        TryParse(candidate, out var newer) &&
        TryParse(running, out var current) &&
        newer.CompareTo(current) > 0;

    public override string ToString() =>
        $"{Major}.{Minor}.{Patch}" + (Prerelease is null ? "" : $"-{Prerelease}");
}
