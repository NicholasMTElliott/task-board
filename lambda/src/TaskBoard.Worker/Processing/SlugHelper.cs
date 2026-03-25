using System.Text.RegularExpressions;

namespace TaskBoard.Worker.Processing;

internal static partial class SlugHelper
{
    /// <summary>
    /// Converts a title string into a URL/filename-safe slug.
    /// Lowercase, alphanumeric + hyphens only, no leading/trailing hyphens,
    /// truncated at word boundary to maxLength.
    /// Returns empty string for null/empty/all-special-chars input.
    /// </summary>
    public static string Sanitize(string? title, int maxLength = 30)
    {
        if (string.IsNullOrWhiteSpace(title))
            return string.Empty;

        // Lowercase and replace non-alphanumeric with hyphens
        var slug = NonAlphanumericRegex().Replace(title.ToLowerInvariant(), "-");

        // Collapse consecutive hyphens
        slug = ConsecutiveHyphensRegex().Replace(slug, "-");

        // Trim leading/trailing hyphens
        slug = slug.Trim('-');

        if (slug.Length <= maxLength)
            return slug;

        // Truncate at word boundary (last hyphen before maxLength)
        var truncated = slug[..maxLength];
        var lastHyphen = truncated.LastIndexOf('-');
        return lastHyphen > 0 ? truncated[..lastHyphen] : truncated;
    }

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonAlphanumericRegex();

    [GeneratedRegex(@"-{2,}")]
    private static partial Regex ConsecutiveHyphensRegex();
}
