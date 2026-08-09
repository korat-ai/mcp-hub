using Korat.Domain;
using Korat.GrainInterfaces;
using Korat.Domain.Persistence;

namespace Korat.Grains;

/// <summary>
/// 029: Grain that caches slug→SpaceId lookups. Key = lowercased slug.
/// Caches POSITIVE results only (null re-queries each call so a freshly-assigned slug is
/// picked up without invalidation plumbing).
/// </summary>
public sealed class SpaceSlugGrain(IMetadataRepository repository) : Grain, ISpaceSlugGrain
{
    private SpaceId? _resolved; // null = not yet found or not cached

    public async Task<SpaceId?> ResolveAsync()
    {
        // If we have a cached positive result, return it.
        if (_resolved.HasValue)
            return _resolved;

        // Null result is NOT cached — re-query DB each time so a freshly-assigned slug
        // is visible without grain deactivation or manual invalidation.
        var slug = this.GetPrimaryKeyString();
        var spaceId = await repository.GetSpaceIdBySlugAsync(slug);
        if (spaceId.HasValue)
            _resolved = spaceId; // cache the positive
        return _resolved;
    }
}
