using System.Globalization;

namespace Solo.Shared.Extensibility;

/// <summary>
/// A half-open version range in the manifest's <c>"&gt;=1.0 &lt;2.0"</c> form.
/// </summary>
/// <remarks>
/// Deliberately tiny: a full semver range grammar is a dependency and a
/// surface area, and the only question ever asked here is "may this package
/// load against this contract". Unparseable input is a load failure with the
/// reason, never a silently-permissive range.
/// </remarks>
public sealed record VersionRange(Version? Minimum, Version? ExclusiveMaximum)
{
    public static bool TryParse(string? text, out VersionRange range, out string? error)
    {
        range = new VersionRange(null, null);
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Version range is empty.";
            return false;
        }

        Version? min = null;
        Version? max = null;
        foreach (var token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.StartsWith(">=", StringComparison.Ordinal))
            {
                if (!TryVersion(token[2..], out min))
                {
                    error = $"'{token}' is not a valid lower bound.";
                    return false;
                }
            }
            else if (token.StartsWith('<'))
            {
                if (!TryVersion(token[1..], out max))
                {
                    error = $"'{token}' is not a valid upper bound.";
                    return false;
                }
            }
            else
            {
                error = $"'{token}' is not understood; use '>=1.0 <2.0'.";
                return false;
            }
        }

        if (min is null && max is null)
        {
            error = "Version range names no bounds.";
            return false;
        }

        range = new VersionRange(min, max);
        return true;
    }

    public bool Includes(Version version) =>
        (Minimum is null || version >= Minimum) &&
        (ExclusiveMaximum is null || version < ExclusiveMaximum);

    public override string ToString() => string.Join(' ',
        new[]
        {
            Minimum is null ? null : $">={Minimum.ToString(2)}",
            ExclusiveMaximum is null ? null : $"<{ExclusiveMaximum.ToString(2)}",
        }.Where(part => part is not null));

    /// <summary>Accepts "1", "1.0" and "1.0.0" — a manifest shouldn't have to write trailing zeroes.</summary>
    private static bool TryVersion(string text, out Version? version)
    {
        version = null;
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var major))
        {
            version = new Version(major, 0);
            return true;
        }

        if (!Version.TryParse(text, out var parsed))
        {
            return false;
        }

        version = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0));
        return true;
    }
}
