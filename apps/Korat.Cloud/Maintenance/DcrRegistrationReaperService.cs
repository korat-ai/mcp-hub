using System.Globalization;
using Korat.Cloud.Web.Oauth;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Korat.Cloud.Maintenance;

/// <summary>
/// Space-MCP inc-2b, Task 6: background sweep that hard-deletes NEVER-CONSENTED (and
/// revoked/expired-only, MF-3) DCR clients older than
/// <see cref="SpaceMcpDcrOptions.UnconsentedTtlMinutes"/> — the TTL half of the open-DCR bounds
/// (the unconsented-cap is the other half — see <see cref="Korat.Cloud.Web.Oauth.IUnconsentedDcrClientCounter"/>).
/// Mirrors <see cref="McpServerReaperService"/>: a <see cref="BackgroundService"/> (Orleans
/// reminders are not configured in this silo), first sweep on the first tick, best-effort
/// try/catch at the top AND per-item (mirrors <see cref="SessionReaperService"/>'s per-item
/// try/catch — one row failing to delete must not abort the rest of the sweep), idempotent so
/// concurrent silos are harmless.
///
/// A client is deleted iff ALL hold: it is DCR-marked (<c>korat:dcr</c> present) — so the seeded
/// pre-registered client and any future OIDC client are NEVER touched (they never carry the
/// marker); its <c>registered_at</c> is older than the TTL; and it has ZERO *valid* authorizations.
///
/// MF-3 (plan-review, fable IL-decompiled): <c>IOpenIddictAuthorizationManager.
/// FindByApplicationIdAsync(id, ct)</c> returns ALL authorizations regardless of status — a client
/// whose consent was later REVOKED still has a (revoked) authorization and would be kept forever
/// if that API drove the "consented?" check. This sweep instead uses the status-filtered
/// <c>FindAsync(subject: null, client: id, status: Statuses.Valid, type: null, scopes: null, ct)</c>
/// overload (verified against the installed OpenIddict 7.5.0 EF Core store: its FindAsync applies
/// `Where(a => a.Status == status)` at the query level) — ONLY a currently-VALID authorization
/// counts as "consented ⇒ keep". A DCR client past TTL whose only authorization is revoked (or
/// expired/rejected, or absent) IS reaped.
///
/// This is what bounds the DCR-re-registration churn (spec open-q #3): Claude/Cursor re-register
/// per launch, minting a new client_id each time; the un-consented (and revoked-only) rows
/// self-expire, so total DCR row growth stays bounded even though we do NOT dedup by client_name
/// at registration time (dedup is DEFERRED — see the plan's DCR-churn note). A live-consented
/// re-registering client's rows are bounded only by the unconsented cap + the deferred console
/// cleanup (consented rows never count toward that cap at all — see
/// <see cref="Korat.Cloud.Web.Oauth.SpaceMcpDcrOptions.MaxUnconsentedClients"/>).
///
/// Registration-flood-DoS hardening: <see cref="SpaceMcpDcrOptions.UnconsentedTtlMinutes"/> moved
/// from hours to MINUTES (default 15 — long enough to cover a first-time interactive sign-in +
/// consent, see the option's own doc comment) and the sweep now runs every
/// <see cref="SpaceMcpDcrOptions.SweepIntervalMinutes"/> (default 5, was hourly) — the unconsented
/// count is now the PRIMARY register-cap gate, so junk must drain fast enough to keep that budget
/// from staying pinned under a sustained flood; sitting on an hour-old sweep cadence would leave
/// the cap filled for up to an hour after a burst even though every junk row is long past TTL. At
/// these defaults, junk drains within roughly one TTL window (~15-20 minutes) of a burst ending.
/// </summary>
public sealed class DcrRegistrationReaperService(
    IServiceScopeFactory serviceScopeFactory,
    SpaceMcpDcrOptions options,
    ILogger<DcrRegistrationReaperService> logger) : BackgroundService
{
    /// <summary>Read once from the injected options at construction — mirrors how every other
    /// value on <see cref="SpaceMcpDcrOptions"/> here is consumed via the same <c>options</c>
    /// instance (a plain singleton, not per-request-resolved), so this is equivalent to reading
    /// <c>options.SweepIntervalMinutes</c> fresh on every tick in practice.
    ///
    /// FIX 4 (fable holistic review NIT): <c>Math.Max(1, …)</c> clamps a misconfigured
    /// <c>&lt;= 0</c> value — <see cref="PeriodicTimer"/>'s constructor throws
    /// <see cref="ArgumentOutOfRangeException"/> for a non-positive period, and this service runs
    /// under <see cref="BackgroundServiceExceptionBehavior.StopHost"/> (the default), so an
    /// unclamped config typo would take the whole silo down instead of merely running the sweep
    /// more often than intended.</summary>
    private readonly TimeSpan _sweepInterval = TimeSpan.FromMinutes(Math.Max(1, options.SweepIntervalMinutes));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // First sweep on the first tick (not immediately) so the silo is fully up.
        using var timer = new PeriodicTimer(_sweepInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // Best-effort — a sweep failure must never crash the silo; retry next tick.
                logger.LogError(ex, "DCR registration reaper sweep failed");
            }
        }
    }

    /// <summary>Resolves a scope (OpenIddict managers are scoped) and runs the sweep core.</summary>
    internal async Task SweepAsync(CancellationToken ct)
    {
        using var scope = serviceScopeFactory.CreateScope();
        var apps = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var auths = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();
        await SweepCoreAsync(apps, auths, options, logger, ct);
    }

    /// <summary>The unit-testable core: managers passed in (mirrors SessionReaperService.SweepAsync).
    /// Returns the number of DCR clients deleted.</summary>
    internal static async Task<int> SweepCoreAsync(
        IOpenIddictApplicationManager applications,
        IOpenIddictAuthorizationManager authorizations,
        SpaceMcpDcrOptions options,
        ILogger logger,
        CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(options.UnconsentedTtlMinutes);

        // TWO passes, because Npgsql does NOT support MARS (multiple active result sets): the
        // ListAsync stream holds ONE open reader for the whole enumeration, so running any other
        // DB query while it is live throws NpgsqlOperationInProgressException ("A command is
        // already in progress"). A production regression showed the reaper could crash on every sweep on
        // Postgres, never reaping; InMemory tests never surfaced it because that provider
        // materializes (no open reader). Pass 1 walks ListAsync and collects DCR-marked, past-TTL
        // candidates — GetPropertiesAsync/GetIdAsync read already-materialized columns of the
        // current row (NO new query), so they are safe inside the loop. Pass 2 runs the
        // per-candidate valid-authorization query only AFTER the ListAsync reader is drained.
        var candidates = new List<(object App, string Id)>();
        await foreach (var app in applications.ListAsync(count: null, offset: null, ct))
        {
            ct.ThrowIfCancellationRequested();
            var props = await applications.GetPropertiesAsync(app, ct);

            // (a) DCR-marked only — the seeded/OIDC clients never carry the marker.
            if (!props.ContainsKey(KoratOAuthConstants.DcrMarkerProperty))
                continue;

            // (b) older than the TTL. A missing/malformed timestamp is LEFT ALONE (fail-safe:
            // never delete a row we can't age).
            if (!props.TryGetValue(KoratOAuthConstants.DcrRegisteredAtProperty, out var tsEl)
                || tsEl.ValueKind != System.Text.Json.JsonValueKind.String
                || !DateTimeOffset.TryParse(tsEl.GetString(), CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out var registeredAt)
                || registeredAt > cutoff)
                continue;

            candidates.Add((app, (await applications.GetIdAsync(app, ct))!));
        }

        // Pass 2 — the ListAsync reader is now closed, so the authorization query is legal.
        // (c) zero VALID authorizations ⇒ never consented (or consented-then-revoked/expired) ⇒
        // junk. MF-3: status-filtered query — ANY non-Valid authorization does NOT count as "keep";
        // any Statuses.Valid authorization DOES ⇒ keep indefinitely. Each FindAsync is awaited and
        // its enumerator disposed (via break/exhaustion) before the next, so no two readers overlap.
        var toDelete = new List<(object App, string Id)>();
        foreach (var (app, appId) in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var hasValidAuthorization = false;
            await foreach (var _ in authorizations.FindAsync(
                subject: null, client: appId, status: Statuses.Valid, type: null, scopes: null, ct))
            {
                hasValidAuthorization = true;
                break;
            }
            if (!hasValidAuthorization)
                toDelete.Add((app, appId));
        }

        var deleted = 0;
        foreach (var (app, id) in toDelete)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await applications.DeleteAsync(app, ct);
                deleted++;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // One row failing to delete (e.g. a concurrent modification) must not abort the
                // rest of the sweep — mirrors SessionReaperService's per-item best-effort pattern.
                logger.LogWarning(ex, "Failed to reap DCR client id={Id}", id);
            }
        }

        if (deleted > 0)
            logger.LogInformation("DCR reaper deleted {Deleted} never-consented/revoked-only registration(s) older than {TtlMinutes}m",
                deleted, options.UnconsentedTtlMinutes);
        return deleted;
    }
}
