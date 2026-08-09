using System.Text;

namespace Korat.Mcp;

/// <summary>
/// Space-MCP (increment 1, Task 4): verbatim port of
/// <c>apps/Korat.Cli/Mcp/Aggregation/ToolNamespacer.cs</c> — namespace changed only, logic
/// byte-for-byte identical. Builds collision-free, stable namespaced tool names:
/// "&lt;slug&gt;__&lt;tool&gt;".
/// </summary>
public static class ToolNamespacer
{
    public const string Separator = "__";
    private const int MaxNameLength = 64;

    public static string Slug(string displayName, string serverId)
    {
        var sb = new StringBuilder(displayName.Length);
        foreach (var ch in displayName.ToLowerInvariant())
        {
            var mapped = char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_';
            // Collapse runs of '_' so a slug can NEVER contain the "__" namespace separator —
            // otherwise a displayName like "iPhone (test)" → "iphone__test" would mis-route in
            // ToolNamespacer.TrySplit (which splits on the first "__"). (agent-DX bug fix)
            if (mapped == '_' && sb.Length > 0 && sb[^1] == '_') continue;
            sb.Append(mapped);
        }
        var slug = sb.ToString().Trim('_');
        return string.IsNullOrEmpty(slug) ? $"srv-{Short(serverId)}" : slug;
    }

    /// <summary>Returns a slug unique within <paramref name="taken"/>, registering it.</summary>
    public static string UniqueSlug(string displayName, string serverId, HashSet<string> taken)
    {
        var baseSlug = Slug(displayName, serverId);
        var slug = baseSlug;
        if (taken.Contains(slug))
            slug = $"{baseSlug}-{Short(serverId)}";
        taken.Add(slug);
        return slug;
    }

    public static string Namespaced(string slug, string toolName)
    {
        var combined = slug + Separator + toolName;
        if (combined.Length <= MaxNameLength) return combined;
        // Truncate the slug, not the tool name (tool name carries the real meaning).
        var room = MaxNameLength - Separator.Length - toolName.Length;
        var keep = Math.Max(3, room);
        var truncated = slug[..Math.Min(slug.Length, keep)].TrimEnd('_');
        // If the truncation boundary lands right after a run of '_', the slice above ends
        // in '_' — appending the "__" separator would then produce 2+ underscores in a row,
        // which TrySplit (splits on the FIRST "__") mis-parses: either the tool name gains a
        // spurious leading '_', or the slug it returns no longer matches the one actually
        // registered in the routing table. TrimEnd('_') above guards that; re-guard here for
        // the degenerate case where trimming empties the slug entirely (e.g. a truncation
        // boundary landing inside an all-underscore prefix).
        if (truncated.Length == 0) truncated = "slug";
        return truncated + Separator + toolName;
    }

    public static string RequestAccessTool(string slug) => "request-access" + Separator + slug;

    public static bool TrySplit(string namespaced, out string slug, out string tool)
    {
        var idx = namespaced.IndexOf(Separator, StringComparison.Ordinal);
        if (idx <= 0) { slug = ""; tool = ""; return false; }
        slug = namespaced[..idx];
        tool = namespaced[(idx + Separator.Length)..];
        return true;
    }

    private static string Short(string id) =>
        new string(id.Where(char.IsAsciiLetterOrDigit).Take(8).ToArray());
}
