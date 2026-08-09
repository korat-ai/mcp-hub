using System.Text.RegularExpressions;

namespace Korat.Domain;

/// <summary>
/// Rules for generating URL-safe space slugs from display names.
/// </summary>
public static class SlugRules
{
    private static readonly Regex NonSlugChars = new(@"[^a-z0-9-]", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex MultiDash = new(@"-{2,}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Converts a display name to a URL-safe slug: lowercase, alphanumeric + hyphens, max 64 chars.
    /// Returns an empty string if the input yields no slug-safe characters.
    /// </summary>
    public static string Slugify(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            return string.Empty;

        // Lowercase, replace spaces/underscores/dots with dash, strip non-slug chars
        var slug = displayName.ToLowerInvariant();
        slug = slug.Replace(' ', '-').Replace('_', '-').Replace('.', '-');
        slug = NonSlugChars.Replace(slug, string.Empty);
        slug = MultiDash.Replace(slug, "-");
        slug = slug.Trim('-');

        if (slug.Length > 64)
            slug = slug[..64].TrimEnd('-');

        return slug;
    }
}
