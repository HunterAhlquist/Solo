using System.Text.RegularExpressions;

namespace Solo.Shared;

/// <summary>
/// The <c>||PROJECT-BOARD-N||</c> task reference that descriptions and comments
/// carry. One grammar with two readers — the client turns references into links,
/// the API re-points them when a task's serial number changes under it — so the
/// two can never disagree about what counts as a reference.
/// </summary>
public static class TaskReferences
{
    /// <summary>
    /// The delimiter both ends of a reference carry. Cheap enough for a SQL
    /// prefilter, which is how the API avoids reading every description it owns.
    /// </summary>
    public const string Delimiter = "||";

    private static readonly Regex Token = new(
        @"\|\|([A-Za-z0-9]{1,8})-([A-Za-z0-9]{1,8})-([0-9]{1,9})\|\|",
        RegexOptions.Compiled);

    /// <summary>
    /// A parsed reference. Aliases are normalized to upper case by
    /// <see cref="Of"/> and <see cref="Replace"/>, which is what makes two
    /// references written in different cases compare equal — construct one any
    /// other way and that no longer holds.
    /// </summary>
    public readonly record struct Reference(string ProjectAlias, string BoardAlias, int Number)
    {
        public static Reference Of(string projectAlias, string boardAlias, int number) =>
            new(Aliases.Normalize(projectAlias), Aliases.Normalize(boardAlias), number);

        public string Serial => Aliases.Serial(ProjectAlias, BoardAlias, Number);
    }

    /// <summary>Rewrites every reference in the text through <paramref name="replacement"/>.</summary>
    public static string Replace(string markdown, Func<Reference, string> replacement) =>
        Token.Replace(markdown, match => replacement(Parse(match)));

    /// <summary>
    /// Re-points references to <paramref name="from"/> at <paramref name="to"/>.
    /// Everything else — other references, and every other character — comes back
    /// exactly as it was written: this edits prose someone else authored, so it
    /// touches only what it must. Matching is by parsed value, so a lower-case or
    /// zero-padded reference is caught too and lands in canonical form.
    /// </summary>
    public static string Retarget(string markdown, Reference from, Reference to) =>
        Token.Replace(markdown, match =>
            Parse(match) == from ? $"{Delimiter}{to.Serial}{Delimiter}" : match.Value);

    private static Reference Parse(Match match) => Reference.Of(
        match.Groups[1].Value, match.Groups[2].Value, int.Parse(match.Groups[3].Value));
}
