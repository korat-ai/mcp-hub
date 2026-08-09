using Korat.Domain;
using Korat.GrainInterfaces;
using Korat.Domain.Persistence;
using Orleans;

namespace Korat.Cloud.Web.Spaces;

/// <summary>
/// Lazily assigns a URL-safe slug to a Space on first use (owner opens inference page or issues a key).
/// The slug is immutable once set. Lookup goes through ISpaceSlugGrain (caches positives).
/// Per plan §①.0 #1: accepts either the lowercased slug OR a raw 32-hex SpaceId in the gateway path.
/// </summary>
public sealed class SpaceSlugService(
    IClusterClient clusterClient,
    IMetadataRepository repository)
{
    /// <summary>
    /// Returns the slug for the space, assigning one if not yet set.
    /// Assignment is idempotent: concurrent callers converge to the same slug.
    /// </summary>
    public async Task<string> GetOrAssignSlugAsync(SpaceId spaceId, string displayName, CancellationToken ct)
    {
        // Fast path: slug already assigned.
        var existing = await repository.GetSpaceSlugAsync(spaceId, ct);
        if (existing is not null)
            return existing;

        // Generate a candidate slug from the display name.
        var candidate = SlugRules.Slugify(displayName);
        if (string.IsNullOrEmpty(candidate))
            candidate = "space";

        // Try to set; if taken (unique constraint violation → false), add hex suffix.
        if (!await repository.TrySetSpaceSlugAsync(spaceId, candidate, ct))
        {
            // Suffixed fallback: "space-<first6hex>" — virtually always unique.
            var suffix = spaceId.Value[..6].ToLowerInvariant();
            candidate = $"{candidate[..Math.Min(candidate.Length, 57)]}-{suffix}";
            if (!await repository.TrySetSpaceSlugAsync(spaceId, candidate, ct))
            {
                // Final fallback: just the hex prefix.
                candidate = $"space-{suffix}";
                await repository.TrySetSpaceSlugAsync(spaceId, candidate, ct);
            }
        }

        // Re-read what actually got written (another silo may have won the race).
        return await repository.GetSpaceSlugAsync(spaceId, ct) ?? candidate;
    }

    /// <summary>
    /// Resolves a path segment to a SpaceId.
    /// Accepts: 32-hex SpaceId (raw) OR a slug (via ISpaceSlugGrain positive-cache).
    /// Returns null if the segment resolves to nothing (unknown slug, not a valid hex id).
    /// </summary>
    public async Task<SpaceId?> ResolveSpaceSegmentAsync(string segment, CancellationToken ct)
    {
        // 32-hex raw SpaceId path — rolling-safe (works before slug assignment).
        if (segment.Length == 32 && IsHex(segment))
            return new SpaceId(segment);

        // Slug path — through the grain (positive-cache).
        var slug = segment.ToLowerInvariant();
        var grain = clusterClient.GetGrain<ISpaceSlugGrain>(slug);
        return await grain.ResolveAsync();
    }

    private static bool IsHex(string s)
    {
        foreach (var c in s)
        {
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                return false;
        }
        return true;
    }
}
