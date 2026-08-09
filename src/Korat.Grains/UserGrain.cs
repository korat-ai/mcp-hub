using Korat.Domain.Auth;
using Korat.Domain.Persistence;
using Korat.GrainInterfaces;

namespace Korat.Grains;

/// <summary>
/// Зерно пользователя: хранит in-memory-копию профильных данных, обеспечивая
/// "grains-are-the-cache" инвариант: все чтения И записи профиля проходят через это зерно.
/// Endpoints must NOT query KoratDbContext for profile data — use GetAsync() instead.
///
/// Write path (Postgres): single-round-trip parameterised UPDATE via ExecuteUpdateAsync,
/// no read-modify-write race. After the UPDATE the grain re-reads the row to refresh _state.
///
/// Write path (InMemory tests): EF Core InMemory does not support ExecuteUpdateAsync. The
/// grain detects the InMemory provider at runtime and falls back to the change-tracking path.
/// This path is correct for sequential test execution (enforced by DisableTestParallelization
/// in the integration-test assembly) but does not provide the same atomicity guarantee as
/// the Postgres path.
/// </summary>
public sealed class UserGrain(IMetadataRepository repository) : Grain, IUserGrain
{
    // In-memory cache. Populated on OnActivateAsync; refreshed after each write.
    private User? _state;

    // Cached once on activation so we do not re-parse on every call.
    private UserId _userId;

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _userId = new UserId(Guid.ParseExact(this.GetPrimaryKeyString(), "N"));
        _state = await repository.GetUserAsync(_userId, cancellationToken);
        await base.OnActivateAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<User?> GetAsync()
    {
        // _state is populated by OnActivateAsync; return the cached value directly.
        // If the grain was activated with no matching DB row, _state is null and the
        // caller (GET /api/auth/me) maps null → 401.
        return Task.FromResult(_state);
    }

    /// <inheritdoc />
    public async Task<User> UpdateDisplayNameAsync(string displayName)
    {
        var updated = await repository.UpdateUserDisplayNameAsync(_userId, displayName);
        _state = updated;
        return updated;
    }

    /// <inheritdoc />
    public async Task<User> UpdatePrimaryEmailAsync(string newEmail)
    {
        // The email has already been committed to the database by EmailChangeService.ConfirmAsync.
        // This grain method's sole responsibility is to reload the authoritative row and refresh
        // the grain's in-memory cache so subsequent GetAsync() calls return the correct email
        // without a database round-trip (grains-are-the-cache invariant).
        //
        // We do NOT write to the database here — doing so would create a race window between
        // the service's atomic UPDATE and this grain call, and could silently re-set the email
        // to a value that has since changed. The grain reads back the DB row to ensure it
        // reflects whatever the DB actually contains, not the value we were passed.
        var refreshed = await repository.ReloadUserAsync(_userId);
        _state = refreshed;
        return refreshed;
    }
}
